using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// State 1 — the existing Alice Voice web relay (ws://localhost:9876).
/// Speaks the same WebSocket protocol as the web app: sends 16 kHz mono Int16
/// PCM frames for STT, receives transcript/reply_text events and TTS audio.
/// </summary>
public sealed class RelayBackend : IVoiceBackend
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public VoiceBackendKind Kind => VoiceBackendKind.Relay;
    public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(VoiceBackendKind.Relay);

    public event Action<BackendEvent>? EventReceived;
    public event Action<byte[]>? AudioReceived;

    public bool IsConnected => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _ws.ConnectAsync(new Uri(settings.RelayUrl), _cts.Token);
        Log.Information("Relay backend connected at {Url}", settings.RelayUrl);
        _receiveLoop = Task.Run(() => ReceiveLoop(_cts.Token), _cts.Token);
    }

    public async Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default)
    {
        var bytes = new byte[pcm16k.Length * sizeof(short)];
        Buffer.BlockCopy(pcm16k, 0, bytes, 0, bytes.Length);
        await SendBinaryAsync(bytes, ct);
    }

    public async Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        var msg = new Dictionary<string, object?> { ["type"] = type };
        if (payload is not null) msg["payload"] = payload;
        await SendTextAsync(JsonSerializer.Serialize(msg), ct);
    }

    private async Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[65536];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var data = new byte[result.Count];
                Array.Copy(buffer, data, result.Count);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    AudioReceived?.Invoke(data);
                }
                else
                {
                    var text = Encoding.UTF8.GetString(data);
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var type = doc.RootElement.GetProperty("type").GetString() ?? "unknown";
                        var payload = doc.RootElement.TryGetProperty("payload", out var p) ? (JsonElement?)p : null;
                        var textVal = payload?.TryGetProperty("text", out var t) == true ? t.GetString() : null;
                        EventReceived?.Invoke(new BackendEvent(type, textVal));
                    }
                    catch (JsonException ex)
                    {
                        Log.Warning(ex, "Unparseable relay message: {Text}", text);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Log.Warning(ex, "Relay WebSocket closed"); }
        finally { Log.Information("Relay receive loop ended"); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
        _sendLock.Dispose();
        if (_receiveLoop is not null) { try { await _receiveLoop; } catch { } }
        _cts?.Dispose();
    }
}
