using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;
using AliceVoice.Core.Chat;

namespace AliceVoice.Core.Voice;

/// <summary>
/// The alice_voice Hermes agent backend. Uses the agent's voice settings and
/// provider/model settings (hermes provider, model = alice_voice). Talks to the
/// Hermes API server's OpenAI-compatible /p/alice_voice/v1 endpoint over plain
/// HTTP — no relay, no WebSocket. STT is done locally on the GPU via the
/// faster-whisper bridge, then the transcript is sent to the agent via AskAsync.
/// </summary>
public sealed class AliceVoiceAgentBackend : IVoiceBackend
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _sttScript;
    private string _apiKey = "";

    public AliceVoiceAgentBackend(string? sttScript = null)
    {
        _sttScript = sttScript ?? Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "faster_whisper_stt.py");
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceVoiceAgent;
    public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(VoiceBackendKind.AliceVoiceAgent);

    public event Action<BackendEvent>? EventReceived;
    public event Action<byte[]>? AudioReceived;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _apiKey = ApiKeyResolver.Resolve();

        // Verify the agent is reachable via the API server.
        var url = $"{settings.AgentEndpoint}/p/alice_voice/v1/models";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        IsConnected = resp.IsSuccessStatusCode;
        Log.Information("alice_voice agent backend connected at {Url} (reachable: {Ok})",
            settings.AgentEndpoint, IsConnected);

        EventReceived?.Invoke(new BackendEvent("backend_ready",
            IsConnected ? "alice_voice agent ready" : "alice_voice agent not reachable"));
    }

    public async Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default)
    {
        // STT: transcribe locally on the GPU via faster-whisper, then ask the agent.
        var text = await TranscribeAsync(pcm16k, ct);
        if (string.IsNullOrWhiteSpace(text)) return;

        EventReceived?.Invoke(new BackendEvent("transcript", text));
        Log.Information("STT: {Text}", text);

        var reply = await AskAsync(text, ct);
        if (!string.IsNullOrWhiteSpace(reply))
            EventReceived?.Invoke(new BackendEvent("reply_text", reply));
    }

    private async Task<string> TranscribeAsync(short[] pcm16k, CancellationToken ct)
    {
        var pcmBytes = new byte[pcm16k.Length * sizeof(short)];
        Buffer.BlockCopy(pcm16k, 0, pcmBytes, 0, pcmBytes.Length);

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_sttScript);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(Settings.SttModel);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add("cuda");
        psi.ArgumentList.Add("--lang");
        psi.ArgumentList.Add("en");

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        await proc.StandardInput.BaseStream.WriteAsync(pcmBytes, ct);
        proc.StandardInput.Close();

        var text = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
        await proc.WaitForExitAsync(ct);
        return text;
    }

    public async Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        switch (type)
        {
            case "reset":
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Agent conversation reset"));
                break;
            case "switch_model":
                EventReceived?.Invoke(new BackendEvent("model_info", $"Model: {Settings.Model}"));
                break;
            case "set_voice":
                EventReceived?.Invoke(new BackendEvent("voice_set", $"Voice: {Settings.TtsVoice}"));
                break;
            default:
                Log.Information("Agent backend command {Type} (no-op)", type);
                break;
        }
    }

    /// <summary>Send a text prompt to the alice_voice agent and raise the reply.</summary>
    public async Task<string> AskAsync(string prompt, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var url = $"{Settings.AgentEndpoint}/p/alice_voice/v1/chat/completions";
            var body = new
            {
                model = "alice_voice",
                messages = new[] { new { role = "user", content = prompt } },
                stream = false
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("Agent chat failed: {Code} {Body}", (int)resp.StatusCode, raw);
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(raw);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            EventReceived?.Invoke(new BackendEvent("reply_text", reply));
            return reply;
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        _http.Dispose();
        _lock.Dispose();
    }
}
