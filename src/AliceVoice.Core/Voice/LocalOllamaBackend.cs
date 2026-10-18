using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// State 3 — local Ollama backend. Fully local, no relay: faster-whisper (GPU)
/// for STT, Ollama for the LLM, SAPI for instant TTS. Magnitude faster than the
/// relay round-trip in both directions.
/// </summary>
public sealed class LocalOllamaBackend : IVoiceBackend
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILocalTts _tts;
    private readonly string _sttScript;
    private CancellationTokenSource? _cts;

    public LocalOllamaBackend(ILocalTts tts, string? sttScript = null)
    {
        _tts = tts;
        _sttScript = sttScript ?? Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "faster_whisper_stt.py");
    }

    public VoiceBackendKind Kind => VoiceBackendKind.LocalOllama;
    public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(VoiceBackendKind.LocalOllama);

    public event Action<BackendEvent>? EventReceived;
    public event Action<byte[]>? AudioReceived;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Verify Ollama is reachable.
        using var resp = await _http.GetAsync($"{settings.OllamaUrl}/api/tags", ct);
        resp.EnsureSuccessStatusCode();
        IsConnected = true;
        Log.Information("Local Ollama backend connected at {Url}, model {Model}",
            settings.OllamaUrl, settings.Model);

        EventReceived?.Invoke(new BackendEvent("backend_ready",
            $"Ollama backend ready — model {Settings.Model}, STT {Settings.SttEngine}, TTS {Settings.TtsEngine}"));
    }

    public async Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default)
    {
        // STT: transcribe locally on the GPU via faster-whisper, then ask Ollama.
        var text = await TranscribeAsync(pcm16k, ct);
        if (string.IsNullOrWhiteSpace(text)) return;

        EventReceived?.Invoke(new BackendEvent("transcript", text));
        Log.Information("STT: {Text}", text);

        var reply = await AskAsync(text, ct);
        if (!string.IsNullOrWhiteSpace(reply))
        {
            // TTS: speak the reply instantly via SAPI.
            _tts.Speak(reply);
        }
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
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Ollama conversation reset"));
                break;
            case "switch_model":
                if (payload is not null)
                {
                    var json = JsonSerializer.Serialize(payload);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("model", out var m))
                        Settings = Settings with { Model = m.GetString() ?? Settings.Model };
                }
                EventReceived?.Invoke(new BackendEvent("model_info", $"Model: {Settings.Model}"));
                break;
            case "set_voice":
                if (payload is not null)
                {
                    var json = JsonSerializer.Serialize(payload);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("voice", out var v))
                        Settings = Settings with { TtsVoice = v.GetString() ?? Settings.TtsVoice };
                }
                EventReceived?.Invoke(new BackendEvent("voice_set", $"Voice: {Settings.TtsVoice}"));
                break;
            default:
                Log.Information("Ollama backend command {Type} (no-op)", type);
                break;
        }
    }

    /// <summary>Send a text prompt to the Ollama model and raise the reply.</summary>
    public async Task<string> AskAsync(string prompt, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var body = new
            {
                model = Settings.Model,
                prompt,
                stream = false
            };

            using var resp = await _http.PostAsJsonAsync($"{Settings.OllamaUrl}/api/generate", body, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var reply = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

            EventReceived?.Invoke(new BackendEvent("reply_text", reply));
            return reply;
        }
        finally { _lock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        IsConnected = false;
        _http.Dispose();
        _lock.Dispose();
        _cts?.Dispose();
    }
}
