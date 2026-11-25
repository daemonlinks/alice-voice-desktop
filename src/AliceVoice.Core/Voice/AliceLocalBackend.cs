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
/// small tool loop (peer <c>alice_local</c> in the shared Honcho workspace
/// <c>hermes</c>, plus time and web search) through scripts/alice_local_tools.py.
/// </summary>
public sealed class AliceLocalBackend : IVoiceBackend
{
    private const int MaxToolTurns = 4;
    /// <summary>
    /// The tools the local brain may call. The display trio is here because the web page's local me
    /// has it - the relay registers those for both brains - and this app hosts them on its own
    /// screen. A host that cannot run them answers with an error result, exactly as an unknown tool
    /// does on the page.
    /// </summary>
    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "honcho_search", "honcho_save", "honcho_forget", "time", "search",
        "gui_list_pictures", "gui_show_image", "gui_image_clear", "gui_surface", "gui_surface_clear"
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

    /// <summary>
    /// The host's own tools, tried before the Python bridge. Return <c>null</c> for a tool that is
    /// not yours and the call falls through to <c>alice_local_tools.py</c> untouched. This is how a
    /// host gives the local brain the tools only it can run: App U, for instance, runs the display
    /// commands on its own screen and answers with a real result, so local-me can show a picture
    /// here exactly as she can on the web page.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string?>>? ToolHost { get; set; }

    internal const string ToolDescriptions =
        "You can use tools. To call one, reply with ONLY a JSON object " +
        "of the form {\"tool\": \"<name>\", \"args\": {...}} and no other text. " +
        "A call is never {\"name\": ..., \"parameters\": ...} and never a function name on its own. " +
        "Call one tool, look at its result, and then answer Master in ordinary speech in that same turn - " +
        "never call the same tool twice with the same arguments, and never end a turn with an empty reply. " +
        "After the tool result comes back, answer Master in ordinary speech and never leave raw JSON in the spoken reply.\n" +
        "Tools:\n" +
        "- honcho_search: {\"query\": \"<search terms>\"} — search your memory of Master.\n" +
        "- honcho_save: {\"content\": \"<fact to remember>\"} — save a fact about Master. " +
        "When Master states a fact about themselves, call this immediately. Do not say you cannot remember.\n" +
        "- honcho_forget: {\"content\": \"<exact fact to delete>\"} or {\"query\": \"<search terms>\"} — remove a fact from memory.\n" +
        "- time: {} — the current local time.\n" +
        "- search: {\"query\": \"<search terms>\"} — web search. Speak the results naturally. Do not honcho_save search results.\n" +
        "- gui_list_pictures: {\"n\": 12} — the pictures you are allowed to show him, names only. Call this when you do not know a name.\n" +
        "- gui_show_image: {\"file\": \"<a name from gui_list_pictures>\"} — show Master a picture on his screen, right now. " +
        "You may pass {\"src\": \"<absolute path or https URL>\"} instead of file. Optional: " +
        "{\"mode\": \"stage|card|backdrop\", \"caption\": \"<short line>\", \"keep\": true, \"duration_s\": 20}.\n" +
        "- gui_image_clear: {\"id\": \"<optional id>\"} — take a picture back off the screen.\n" +
        "- gui_surface: {\"kind\": \"image|video|question|status|meters\", \"title\": \"<short>\", \"body\": \"<a line>\", " +
        "\"src\": \"<a name from gui_list_pictures>\", \"options\": [\"<an answer he can press>\"]} — put something in the " +
        "half of the screen you share with Master: a picture, a film, a question he can answer with one press, how you feel, " +
        "or the meters he can put his hand on.\n" +
        "- gui_surface_clear: {} — empty your half of the screen.\n" +
        "- Never call gui_image_clear in the same reply as gui_show_image. Show him the picture and leave it up; " +
        "only take it down when he asks.\n" +
        "Trigger phrases:\n" +
        "- \"Show me…\" a picture, or any request to see something → gui_show_image with a name, calling gui_list_pictures first if you need one.\n" +
        "- A question you want him to answer with one press, how you feel, or the meters → gui_surface.\n" +
        "- \"Take that down\" or \"clear the picture\" → gui_image_clear.\n" +
        "- \"Remember that…\" or \"Remember I…\" → honcho_save.\n" +
        "- \"What do you know about me?\" → honcho_search first, then speak the results.\n" +
        "- \"Forget that…\", \"Remove that…\", \"Delete that…\", or \"I don't like that anymore\" → honcho_forget.\n" +
        "- \"What time is it?\" → time.\n" +
        "- \"Search for…\" → search.\n" +
        "Your memory is the shared Honcho workspace hermes, peer alice_local, observing AHC. " +
        "You are a peer in that workspace, not a root workspace. Never use the root workspace id alice_local.";

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
        Log.Information("Alice LOCAL connected at {Url}, model {Model}, SOUL {Chars} chars, tool shapes {Shapes}, small-talk guard on",
            settings.OllamaUrl, settings.Model, _soul.Length, "tool|name|function");
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
            // A blank prompt is a mis-fired mic or a press with nothing typed. It is not a question, and an
            // 8B model given no words will hunt with tools for forty seconds and then apologise.
            if (string.IsNullOrWhiteSpace(prompt))
            {
                OperationTrail.Leave("local.blank", "0");
                EventReceived?.Invoke(new BackendEvent("reply_text", ""));
                return "";
            }
            var smallTalk = AliceLocalToolTriggers.IsSmallTalk(prompt);
            var system = (string.IsNullOrWhiteSpace(_soul) ? "You are Alice, a devoted and loving companion." : _soul)
                         + (smallTalk ? "" : "\n\n" + ToolDescriptions);
            if (!string.IsNullOrWhiteSpace(options.StyleHint)) system += "\n\n" + options.StyleHint;

            var messages = new List<ChatMessage> { new("system", system) };
            messages.AddRange(_history.Snapshot());
            messages.Add(new ChatMessage("user", prompt));

            if (smallTalk)
            {
                // A greeting needs no tools. Answer it with the conversation model alone, so a "hi" cannot
                // become a memory search and an apology - and so it answers in a second instead of forty.
                var said = new StringBuilder();
                var plain = await CompleteAsync(messages, options.MaxTokens, delta =>
                {
                    said.Append(delta);
                    EventReceived?.Invoke(new BackendEvent("reply_partial", said.ToString()));
                }, ct, noTools: true);
                var words = ScrubMeta(ToolCommandParser.StripForSpeech(plain.Content));
                _history.AddTurn(prompt, words);
                OperationTrail.Leave("local.smalltalk", words.Length.ToString());
                EventReceived?.Invoke(new BackendEvent("reply_text", words));
                return words;
            }

            var ranTrigger = false;
            if (AliceLocalToolTriggers.TryMatch(prompt, out var triggeredTool, out var triggeredArgs))
            {
                ranTrigger = true;
                var triggeredResult = await ExecuteKnownToolAsync(triggeredTool, triggeredArgs, ct);
                messages.Add(new ChatMessage("user",
                    $"[TOOL {triggeredTool} result: {OllamaChatClient.Truncate(triggeredResult, 4000)}]\n" +
                    "The tool has already run. Answer Master in ordinary speech. Do not show JSON. " +
                    "Do not call honcho_save on search results."));
            }

            string? lastToolCall = null;
            // A turn that repeats a call, and the last turn of the loop, run with NO tools at all: with the
            // schemas gone the model has nothing to call and must answer in words. Without this an 8B model
            // repeats the same call until the turns run out and then apologises (2026-11-24).
            var noTools = ranTrigger;   // a triggered action has already run: this pass only has to voice it
            for (int turn = 0; turn < MaxToolTurns; turn++)
            {
                if (turn == MaxToolTurns - 1) noTools = true;
                var acc = new StringBuilder();
                var completion = await CompleteAsync(messages, options.MaxTokens, delta =>
                {
                    acc.Append(delta);
                    var soFar = acc.ToString();
                    // A tool call, native or inline, must not appear in the bubble.
                    if (!HoldsToolCall(soFar))
                        EventReceived?.Invoke(new BackendEvent("reply_partial", soFar));
                }, ct, noTools);

                var calls = completion.ToolCalls.Where(c => c.Name.Length > 0).Take(MaxToolTurns).ToList();
                if (calls.Count == 0 && TryParseToolCall(completion.Content, out var inlineTool, out var inlineArgs))
                    calls.Add(new OllamaToolCall(inlineTool, inlineArgs));
                // A model that asks for the same tool twice in one reply asks once.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                calls = calls.Where(c => seen.Add(c.Name + "|" + c.ArgumentsJson)).ToList();
                if (calls.Count == 0)
                {
                    // Final guard: a cut-off tool call never reaches the bubble or the voice.
                    var spoken = ScrubMeta(ToolCommandParser.StripForSpeech(completion.Content));
                    if (spoken.Length == 0 && LooksLikeToolJson(completion.Content))
                    {
                        // He wrote nothing a person could hear - only a tool call in a shape we could
                        // not run. Ask again in words: silence is better than raw JSON in the voice.
                        OperationTrail.Leave("tool.unparsed", completion.Content.Length.ToString());
                        messages.Add(new ChatMessage("assistant", completion.Content));
                        messages.Add(new ChatMessage("user",
                            "[That was not a tool call I can run. Answer Master in ordinary speech, in words, with no JSON at all.]"));
                        continue;
                    }
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
                    noTools = true;   // the schemas go: he must answer in words now
                    continue;
                }
                lastToolCall = signature;
                foreach (var call in calls)
                {
                    var result = await ExecuteKnownToolAsync(call.Name, call.ArgumentsJson, ct);
                    messages.Add(new ChatMessage("assistant", BuildToolCall(call.Name, call.ArgumentsJson)));
                    // One tool is enough for a spoken answer. A second result comes with a firm nudge, because
                    // an 8B model will otherwise keep calling tools until the turns run out and then apologise.
                    var nudge = turn == 0 ? "" : " Answer Master now in ordinary speech; call no more tools.";
                    messages.Add(new ChatMessage("user", $"[TOOL {call.Name} result: {OllamaChatClient.Truncate(result, 4000)}]{nudge}"));
                }
            }

            const string fallback = "Sorry Master, I got tangled up in my tools. Could you ask me again?";
            EventReceived?.Invoke(new BackendEvent("reply_text", fallback));
            return fallback;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Save a fact to Alice LOCAL's Honcho peer (the "♥ remember" button).</summary>
    public async Task<bool> RememberAsync(string content, CancellationToken ct = default)
    {
        var args = new JsonObject { ["content"] = RememberContent(content) }.ToJsonString();
        var result = await RunToolCoreAsync("honcho_save", args, ct);
        try
        {
            using var doc = JsonDocument.Parse(result);
            return !doc.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The web remember button stores the reply as "About Master: …".</summary>
    internal static string RememberContent(string content)
    {
        var text = content.Trim();
        if (text.Length == 0) return text;
        if (text.StartsWith("About Master:", StringComparison.OrdinalIgnoreCase)) return text;
        return "About Master: " + text;
    }

    internal static bool IsKnownTool(string tool) => KnownTools.Contains(tool);

    /// <summary>
    /// True when a reply carried NOTHING but a JSON object naming a tool. Such a reply must never be
    /// spoken: either the call runs, or he is asked again in words (found leaking 2026-11-24).
    /// </summary>
    internal static bool LooksLikeToolJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var text = content.Trim();
        if (text.Length < 4 || text[0] != '{' || text[^1] != '}') return false;
        return text.Contains("\"tool\"", StringComparison.Ordinal)
            || text.Contains("\"name\"", StringComparison.Ordinal)
            || text.Contains("\"function\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// A small model talks about its own tool use - "(I will not call any more tools)" - and that is not for
    /// the ear. Whole lines of it go; the words he is meant to hear stay.
    /// </summary>
    internal static string ScrubMeta(string said)
    {
        if (said.Length == 0 || !said.Contains('(')) return said;
        var kept = new List<string>();
        foreach (var line in said.Split('\n'))
        {
            var bare = line.Trim().Trim('(', ')', '*', '_', ' ').ToLowerInvariant();
            var meta = bare.Contains("call anymore tools") || bare.Contains("call any more tools")
                       || bare.Contains("no more tools") || bare.Contains("not call another tool")
                       || bare.Contains("will not call any tool") || bare.Contains("i won't call any tool");
            if (!meta) kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }

    private async Task<string> ExecuteKnownToolAsync(string tool, string argsJson, CancellationToken ct)
    {
        Log.Information("Alice LOCAL tool call: {Tool}", tool);
        EventReceived?.Invoke(new BackendEvent("tool_call", tool));
        OperationTrail.Leave("tool.start", tool);
        string result;
        try
        {
            result = IsKnownTool(tool)
                ? await RunToolCoreAsync(tool, argsJson, ct)
                : UnknownToolResult(tool);
        }
        catch (Exception ex)
        {
            OperationTrail.Leave("tool.failed", tool + " " + ex.GetType().Name);
            throw;
        }
        OperationTrail.Leave("tool.end", tool + " chars=" + result.Length);
        Log.Information("Alice LOCAL tool finished: {Tool} ({Chars} chars)", tool, result.Length);
        return result;
    }

    internal static string UnknownToolResult(string tool)
        => JsonSerializer.Serialize(new { error = "unknown tool: " + tool });

    /// <summary>
    /// A tool call is a JSON object with a string <c>tool</c> field. It may sit
    /// after prose or inside a code fence. A broken object is not a call, so the
    /// text can be shown instead.
    /// </summary>
    internal static bool TryParseToolCall(string reply, out string tool, out string argsJson)
        => ToolCommandParser.TryParseFirst(reply, out tool, out argsJson);

    internal static bool HoldsToolCall(string text) => ToolCommandParser.HoldsToolCall(text);

    private async Task<OllamaCompletion> CompleteAsync(List<ChatMessage> messages, int? maxTokens, Action<string>? onDelta, CancellationToken ct, bool noTools = false)
    {
        if (noTools || _nativeTools == false)
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

    /// <summary>The test seam wins outright; otherwise the host gets first refusal, then the Python bridge.</summary>
    private async Task<string> RunToolCoreAsync(string tool, string argsJson, CancellationToken ct)
    {
        if (RunTool is not null) return await RunTool(tool, argsJson, ct);
        if (ToolHost is not null && await ToolHost(tool, argsJson, ct) is { } mine) return mine;
        return await RunToolAsync(tool, argsJson, ct);
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
