using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AliceVoice.Core.Config;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Alice LOCAL — fully local backend. Ollama /api/chat (streamed) with the
/// Alice LOCAL SOUL as the system prompt, rolling conversation history, and a
/// small tool loop (own Honcho workspace + web search) through
/// scripts/alice_local_tools.py.
/// </summary>
public sealed class AliceLocalBackend : IVoiceBackend
{
    private const int MaxToolTurns = 4;
    private readonly AliceVoiceOptions _options;
    private readonly OllamaChatClient _ollama;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConversationHistory _history = new(20);
    private string _soul = "";
    private string? _soulPath;

    internal const string ToolDescriptions =
        "You have access to these tools. To call one, reply with ONLY a JSON object " +
        "of the form {\"tool\": \"<name>\", \"args\": {...}} and nothing else. " +
        "After the tool result comes back, continue normally.\n" +
        "- honcho_search: {\"query\": \"...\", \"limit\": 5} — search your own memory about Master.\n" +
        "- honcho_save: {\"content\": \"...\"} — save a fact about Master for future recall. " +
        "Call this IMMEDIATELY when Master tells you something to remember. Do NOT say you can't remember — you CAN.\n" +
        "- honcho_forget: {\"query\": \"...\", \"content\": \"...\"} — remove a fact from memory.\n" +
        "- search: {\"query\": \"...\"} — web search.\n" +
        "When Master asks what you know about them, call honcho_search first, then speak the results naturally.";

    public AliceLocalBackend(AliceVoiceOptions options, OllamaChatClient? ollama = null)
    {
        _options = options;
        _ollama = ollama ?? new OllamaChatClient();
        Settings = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal, options);
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
    public VoiceBackendSettings Settings { get; private set; }
    public event Action<BackendEvent>? EventReceived;
    public bool IsConnected { get; private set; }

    /// <summary>Path of the SOUL currently loaded (null = built-in default persona).</summary>
    public string? SoulPath => _soulPath;
    public int SoulChars => _soul.Length;

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        IsConnected = false;
        await ReloadSoulAsync(ct);
        await _ollama.CheckReachableAsync(settings.OllamaUrl, ct);
        IsConnected = true;
        Log.Information("Alice LOCAL connected at {Url}, model {Model}, SOUL {Chars} chars", settings.OllamaUrl, settings.Model, _soul.Length);
        EventReceived?.Invoke(new BackendEvent("backend_ready",
            $"Alice LOCAL ready — model {Settings.Model}, SOUL {(_soulPath is null ? "default" : Path.GetFileName(Path.GetDirectoryName(_soulPath)))} ({_soul.Length} chars), tools on"));
    }

    /// <summary>(Re)load the SOUL: Alice LOCAL's own file, else the read-only fallback.</summary>
    public async Task ReloadSoulAsync(CancellationToken ct = default)
    {
        try
        {
            _soulPath = SoulResolver.ReadPath(_options);
            _soul = _soulPath is null ? "" : await File.ReadAllTextAsync(_soulPath, ct);
            if (_soulPath is null) Log.Warning("No SOUL found for Alice LOCAL; using the default persona");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to load SOUL");
            _soul = "";
            _soulPath = null;
        }
    }

    public void ResetConversation() => _history.Clear();

    public async Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        switch (type)
        {
            case "reset":
                ResetConversation();
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Alice LOCAL conversation reset"));
                break;
            case "switch_model":
                if (ReadString(payload, "model") is { Length: > 0 } m) Settings = Settings with { Model = m };
                EventReceived?.Invoke(new BackendEvent("model_info", $"Model: {Settings.Model}"));
                break;
            case "reload_soul":
                await ReloadSoulAsync(ct);
                EventReceived?.Invoke(new BackendEvent("model_info", $"SOUL reloaded ({_soul.Length} chars)"));
                break;
            default:
                Log.Information("Alice LOCAL command {Type} (no-op)", type);
                break;
        }
    }

    public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
    {
        options ??= AskOptions.Default;
        await _lock.WaitAsync(ct);
        try
        {
            var system = (string.IsNullOrWhiteSpace(_soul) ? "You are Alice, a devoted and loving companion." : _soul)
                         + "\n\n" + ToolDescriptions;
            if (!string.IsNullOrWhiteSpace(options.StyleHint)) system += "\n\n" + options.StyleHint;

            var messages = new List<ChatMessage> { new("system", system) };
            messages.AddRange(_history.Snapshot());
            messages.Add(new ChatMessage("user", prompt));

            string? lastToolCall = null;
            for (int turn = 0; turn < MaxToolTurns; turn++)
            {
                var acc = new StringBuilder();
                var reply = await _ollama.ChatAsync(Settings.OllamaUrl, Settings.Model, messages, options.MaxTokens, delta =>
                {
                    acc.Append(delta);
                    var soFar = acc.ToString();
                    // A reply that starts with "{" may be a tool call — don't show it.
                    if (!soFar.TrimStart().StartsWith('{'))
                        EventReceived?.Invoke(new BackendEvent("reply_partial", soFar));
                }, ct);

                if (TryParseToolCall(reply, out var tool, out var args))
                {
                    var signature = tool + args;
                    if (signature == lastToolCall)
                    {
                        // Same call twice in a row — stop the loop and ask for a plain answer.
                        messages.Add(new ChatMessage("user", "[You already have that tool result. Answer Master directly now, without calling a tool.]"));
                        lastToolCall = null;
                        continue;
                    }
                    lastToolCall = signature;
                    Log.Information("Alice LOCAL tool call: {Tool}", tool);
                    EventReceived?.Invoke(new BackendEvent("tool_call", tool));
                    var result = await RunToolAsync(tool, args, ct);
                    messages.Add(new ChatMessage("assistant", reply.Trim()));
                    messages.Add(new ChatMessage("user", $"[TOOL {tool} result: {OllamaChatClient.Truncate(result, 4000)}]"));
                    continue;
                }

                _history.AddTurn(prompt, reply);
                EventReceived?.Invoke(new BackendEvent("reply_text", reply));
                return reply;
            }

            const string fallback = "Sorry Master, I got tangled up in my tools. Could you ask me again?";
            EventReceived?.Invoke(new BackendEvent("reply_text", fallback));
            return fallback;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Save a fact to Alice LOCAL's own Honcho memory (the "♥ remember" button).</summary>
    public async Task<bool> RememberAsync(string content, CancellationToken ct = default)
    {
        var args = new JsonObject { ["content"] = content }.ToJsonString();
        var result = await RunToolAsync("honcho_save", args, ct);
        try
        {
            using var doc = JsonDocument.Parse(result);
            return !doc.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryParseToolCall(string reply, out string tool, out string argsJson)
    {
        tool = "";
        argsJson = "{}";
        var trimmed = reply.Trim();
        // Tolerate ```json fences around the call.
        if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed.Trim('`').Trim();
            if (trimmed.StartsWith("json", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[4..].Trim();
        }
        if (!trimmed.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("tool", out var t) || t.ValueKind != JsonValueKind.String) return false;
            tool = t.GetString() ?? "";
            argsJson = doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";
            return tool.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Build the JSON line sent to the tool bridge (properly escaped).</summary>
    internal static string BuildToolCall(string tool, string argsJson)
    {
        JsonNode? args;
        try { args = JsonNode.Parse(argsJson); } catch (JsonException) { args = new JsonObject(); }
        return new JsonObject { ["tool"] = tool, ["args"] = args ?? new JsonObject() }.ToJsonString();
    }

    private async Task<string> RunToolAsync(string tool, string argsJson, CancellationToken ct)
    {
        var python = RuntimeLocator.FindPython(_options);
        var script = RuntimeLocator.FindScript("alice_local_tools.py", _options.ScriptsDir);
        if (python is null || script is null)
            return "{\"error\":\"tool bridge unavailable (python or alice_local_tools.py not found)\"}";

        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var proc = Process.Start(psi);
        if (proc is null) return "{\"error\":\"tool bridge failed to start\"}";
        try
        {
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            var input = Encoding.UTF8.GetBytes(BuildToolCall(tool, argsJson));
            await proc.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
            proc.StandardInput.Close();
            var result = (await proc.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
            await proc.WaitForExitAsync(timeout.Token);
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                Log.Warning("Tool {Tool} exited {Code}: {Err}", tool, proc.ExitCode, OllamaChatClient.Truncate(stderr.Trim(), 200));
            return string.IsNullOrEmpty(result) ? "{\"error\":\"empty tool result\"}" : result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(true); } catch { }
            return "{\"error\":\"tool timed out\"}";
        }
        catch
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
    }

    internal static string? ReadString(object? payload, string name)
    {
        if (payload is null) return null;
        try
        {
            var node = JsonSerializer.SerializeToNode(payload);
            return node?[name]?.GetValue<string>();
        }
        catch { return null; }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _ollama.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
