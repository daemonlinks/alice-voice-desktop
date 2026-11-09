using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AliceVoice.Core.Config;
using AliceVoice.Core.Logging;
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
    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "honcho_search", "honcho_save", "honcho_forget", "search"
    };
    private readonly AliceVoiceOptions _options;
    private readonly OllamaChatClient _ollama;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConversationHistory _history = new(20);
    private string _soul = "";
    private string? _soulPath;
    private bool? _nativeTools;

    /// <summary>Test seam. Production runs <c>alice_local_tools.py</c>.</summary>
    internal Func<string, string, CancellationToken, Task<string>>? RunTool { get; set; }

    internal const string ToolDescriptions =
        "You have access to these tools. To call one, reply with a JSON object " +
        "of the form {\"tool\": \"<name>\", \"args\": {...}}. " +
        "The object may follow a short sentence or sit in a code fence. " +
        "After the tool result comes back, answer Master in ordinary speech and do not show the JSON.\n" +
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

    /// <summary>
    /// (Re)load the SOUL: ONLY Alice LOCAL's own local SOUL file. If it does
    /// not exist a neutral starter template is created; no agent SOUL is ever
    /// read. On error the built-in default persona is used.
    /// </summary>
    public async Task ReloadSoulAsync(CancellationToken ct = default)
    {
        try
        {
            var doc = await new SoulStore(_options).LoadAsync(ct);
            _soul = doc.Text;
            _soulPath = doc.Path;
            if (doc.CreatedFromTemplate) Log.Information("Created starter Alice LOCAL SOUL at {Path}", doc.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to load the Alice LOCAL SOUL; using the default persona");
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
                var completion = await CompleteAsync(messages, options.MaxTokens, delta =>
                {
                    acc.Append(delta);
                    var soFar = acc.ToString();
                    // A tool call, native or inline, must not appear in the bubble.
                    if (!HoldsToolCall(soFar))
                        EventReceived?.Invoke(new BackendEvent("reply_partial", soFar));
                }, ct);

                var calls = completion.ToolCalls.Where(c => c.Name.Length > 0).Take(MaxToolTurns).ToList();
                if (calls.Count == 0 && TryParseToolCall(completion.Content, out var inlineTool, out var inlineArgs))
                    calls.Add(new OllamaToolCall(inlineTool, inlineArgs));
                if (calls.Count == 0)
                {
                    var spoken = completion.Content;
                    _history.AddTurn(prompt, spoken);
                    EventReceived?.Invoke(new BackendEvent("reply_text", spoken));
                    return spoken;
                }

                var signature = string.Join("|", calls.Select(c => c.Name + c.ArgumentsJson));
                if (signature == lastToolCall)
                {
                    OperationTrail.Leave("tool.duplicate", calls[0].Name);
                    messages.Add(new ChatMessage("user", "[You already have that tool result. Answer Master directly now, without calling a tool.]"));
                    lastToolCall = null;
                    continue;
                }
                lastToolCall = signature;
                foreach (var call in calls)
                {
                    Log.Information("Alice LOCAL tool call: {Tool}", call.Name);
                    EventReceived?.Invoke(new BackendEvent("tool_call", call.Name));
                    OperationTrail.Leave("tool.start", call.Name);
                    string result;
                    try
                    {
                        result = IsKnownTool(call.Name)
                            ? await (RunTool ?? RunToolAsync)(call.Name, call.ArgumentsJson, ct)
                            : UnknownToolResult(call.Name);
                    }
                    catch (Exception ex)
                    {
                        OperationTrail.Leave("tool.failed", call.Name + " " + ex.GetType().Name);
                        throw;
                    }
                    OperationTrail.Leave("tool.end", call.Name + " chars=" + result.Length);
                    Log.Information("Alice LOCAL tool finished: {Tool} ({Chars} chars)", call.Name, result.Length);
                    messages.Add(new ChatMessage("assistant", BuildToolCall(call.Name, call.ArgumentsJson)));
                    messages.Add(new ChatMessage("user", $"[TOOL {call.Name} result: {OllamaChatClient.Truncate(result, 4000)}]"));
                }
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

    internal static bool IsKnownTool(string tool) => KnownTools.Contains(tool);

    internal static string UnknownToolResult(string tool)
        => JsonSerializer.Serialize(new { error = "unknown tool: " + tool });

    /// <summary>
    /// A tool call is a JSON object with a string <c>tool</c> field. It may sit
    /// after prose or inside a code fence. A broken object is not a call, so the
    /// text can be shown instead.
    /// </summary>
    internal static bool TryParseToolCall(string reply, out string tool, out string argsJson)
    {
        tool = "";
        argsJson = "{}";
        if (string.IsNullOrWhiteSpace(reply)) return false;
        for (var i = 0; i < reply.Length; i++)
        {
            if (reply[i] != '{') continue;
            if (!TrySliceJson(reply, i, out var slice)) continue;
            try
            {
                using var doc = JsonDocument.Parse(slice);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("tool", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var name = t.GetString() ?? "";
                if (name.Length == 0) continue;
                tool = name;
                argsJson = doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";
                return true;
            }
            catch (JsonException) { /* a later object may be the call */ }
            i += Math.Max(0, slice.Length - 1);
        }
        return false;
    }

    internal static bool HoldsToolCall(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith("```")) return true;
        var brace = text.IndexOf('{');
        return brace >= 0 && text.IndexOf("\"tool\"", brace, StringComparison.Ordinal) >= 0;
    }

    private static bool TrySliceJson(string text, int start, out string json)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
        }
        json = "";
        return false;
    }

    private async Task<OllamaCompletion> CompleteAsync(List<ChatMessage> messages, int? maxTokens, Action<string>? onDelta, CancellationToken ct)
    {
        if (_nativeTools == false)
            return await _ollama.CompleteAsync(Settings.OllamaUrl, Settings.Model, messages, maxTokens, onDelta, nativeTools: false, ct);
        try
        {
            var completion = await _ollama.CompleteAsync(Settings.OllamaUrl, Settings.Model, messages, maxTokens, onDelta, nativeTools: true, ct);
            _nativeTools = true;
            return completion;
        }
        catch (HttpRequestException ex) when (OllamaChatClient.ModelRejectsTools(ex.Message))
        {
            _nativeTools = false;
            Log.Information("Ollama model {Model} does not support native tools; using inline JSON tool calls", Settings.Model);
            return await _ollama.CompleteAsync(Settings.OllamaUrl, Settings.Model, messages, maxTokens, onDelta, nativeTools: false, ct);
        }
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
            {
                OperationTrail.Leave("tool.exit", tool + " code=" + proc.ExitCode);
                Log.Warning("Tool {Tool} exited {Code}: {Err}", tool, proc.ExitCode, OllamaChatClient.Truncate(stderr.Trim(), 200));
            }
            return string.IsNullOrEmpty(result) ? "{\"error\":\"empty tool result\"}" : result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            OperationTrail.Leave("tool.timeout", tool);
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
