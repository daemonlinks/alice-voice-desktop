using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Alice LOCAL — fully local backend. Ollama for the LLM with the local SOUL
/// file loaded as the system prompt, faster-whisper (GPU) for STT, SAPI for
/// instant TTS. Has its OWN Honcho workspace (alice_local) plus web search,
/// via the alice_local_tools.py bridge — mirrors the web relay's tool layer.
/// </summary>
public sealed class AliceLocalBackend : IVoiceBackend
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILocalTts _tts;
    private readonly string _sttScript;
    private readonly string _toolsScript;
    private string _soul = "";
    private CancellationTokenSource? _cts;

    private const string ToolDescriptions =
        "You have access to these tools. To call one, reply with ONLY a JSON object " +
        "of the form {\"tool\": \"<name>\", \"args\": {...}} and nothing else. " +
        "After the tool result comes back, continue normally.\n" +
        "- honcho_search: {\"query\": \"...\", \"limit\": 5} — search your own memory about Master.\n" +
        "- honcho_save: {\"content\": \"...\"} — save a fact about Master for future recall. " +
        "Call this IMMEDIATELY when Master tells you something to remember. Do NOT say you can't remember — you CAN.\n" +
        "- honcho_forget: {\"query\": \"...\", \"content\": \"...\"} — remove a fact from memory.\n" +
        "- search: {\"query\": \"...\"} — web search.\n" +
        "When Master asks what you know about them, call honcho_search first, then speak the results naturally.";

    public AliceLocalBackend(ILocalTts tts, string? sttScript = null, string? toolsScript = null)
    {
        _tts = tts;
        _sttScript = sttScript ?? Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "faster_whisper_stt.py");
        _toolsScript = toolsScript ?? Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "alice_local_tools.py");
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
    public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);

    public event Action<BackendEvent>? EventReceived;
    public event Action<byte[]>? AudioReceived;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Load the local SOUL file as the system prompt.
        try
        {
            if (File.Exists(settings.SoulPath))
                _soul = await File.ReadAllTextAsync(settings.SoulPath, ct);
            else
                Log.Warning("SOUL file not found at {Path}", settings.SoulPath);
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load SOUL file"); }

        // Verify Ollama is reachable.
        using var resp = await _http.GetAsync($"{settings.OllamaUrl}/api/tags", ct);
        resp.EnsureSuccessStatusCode();
        IsConnected = true;
        Log.Information("Alice LOCAL backend connected at {Url}, model {Model}, SOUL {SoulChars} chars",
            settings.OllamaUrl, settings.Model, _soul.Length);

        EventReceived?.Invoke(new BackendEvent("backend_ready",
            $"Alice LOCAL ready — model {Settings.Model}, SOUL loaded ({_soul.Length} chars), tools on"));
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
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Alice LOCAL conversation reset"));
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
                Log.Information("Alice LOCAL backend command {Type} (no-op)", type);
                break;
        }
    }

    /// <summary>Send a text prompt to Ollama with the SOUL file as system context, running a tool loop.</summary>
    public async Task<string> AskAsync(string prompt, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var system = string.IsNullOrWhiteSpace(_soul)
                ? "You are Alice, a devoted and loving companion."
                : _soul;
            system += "\n\n" + ToolDescriptions;

            var conversation = prompt;
            for (int turn = 0; turn < 4; turn++)
            {
                var body = new
                {
                    model = Settings.Model,
                    system = system,
                    prompt = conversation,
                    stream = false
                };

                using var resp = await _http.PostAsJsonAsync($"{Settings.OllamaUrl}/api/generate", body, ct);
                resp.EnsureSuccessStatusCode();

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var reply = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

                // If the reply is a tool call, execute it and feed the result back.
                if (TryParseToolCall(reply, out var tool, out var args))
                {
                    Log.Information("Alice LOCAL tool call: {Tool}", tool);
                    var result = await RunToolAsync(tool, args, ct);
                    conversation += $"\n\n[TOOL {tool} result: {result}]";
                    continue;
                }

                EventReceived?.Invoke(new BackendEvent("reply_text", reply));
                return reply;
            }

            return string.Empty;
        }
        finally { _lock.Release(); }
    }

    private static bool TryParseToolCall(string reply, out string tool, out string argsJson)
    {
        tool = "";
        argsJson = "";
        var trimmed = reply.Trim();
        if (!trimmed.StartsWith("{")) return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("tool", out var t)) return false;
            tool = t.GetString() ?? "";
            argsJson = doc.RootElement.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
            return !string.IsNullOrEmpty(tool);
        }
        catch { return false; }
    }

    private async Task<string> RunToolAsync(string tool, string argsJson, CancellationToken ct)
    {
        var call = $"{{\"tool\": \"{tool}\", \"args\": {argsJson}}}";
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_toolsScript);

        using var proc = Process.Start(psi);
        if (proc is null) return "{\"error\":\"tool bridge failed to start\"}";

        await proc.StandardInput.WriteAsync(call.AsMemory(), ct);
        proc.StandardInput.Close();

        var result = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
        await proc.WaitForExitAsync(ct);
        return string.IsNullOrEmpty(result) ? "{\"error\":\"empty tool result\"}" : result;
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
