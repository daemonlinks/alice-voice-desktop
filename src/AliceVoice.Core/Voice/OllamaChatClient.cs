using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AliceVoice.Core.Voice;

public readonly record struct OllamaToolCall(string Name, string ArgumentsJson);

public readonly record struct OllamaCompletion(string Content, IReadOnlyList<OllamaToolCall> ToolCalls);

/// <summary>
/// Streaming client for Ollama's /api/chat. Reads the NDJSON response line by
/// line as it arrives (ResponseHeadersRead) and reports each delta, so the UI
/// can show the reply while it is being generated.
/// </summary>
public sealed class OllamaChatClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>
    /// How long one chat may run before it is reported as a timeout.
    /// Ollama's server <c>keep_alive</c> is intentionally not sent, so a
    /// service-wide value (such as one hour) is left alone.
    /// </summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public OllamaChatClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task CheckReachableAsync(string baseUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Stream a chat completion. Returns the full reply text. Does not request native tools.</summary>
    public async Task<string> ChatAsync(string baseUrl, string model, IReadOnlyList<ChatMessage> messages,
        int? maxTokens, Action<string>? onDelta, CancellationToken ct)
        => (await CompleteAsync(baseUrl, model, messages, maxTokens, onDelta, nativeTools: false, ct)).Content;

    /// <summary>
    /// Stream a chat completion. When <paramref name="nativeTools"/> is set, the request
    /// includes Ollama's <c>tools</c> array. <c>keep_alive</c> is still omitted.
    /// </summary>
    public async Task<OllamaCompletion> CompleteAsync(string baseUrl, string model, IReadOnlyList<ChatMessage> messages,
        int? maxTokens, Action<string>? onDelta, bool nativeTools, CancellationToken ct)
    {
        var options = new Dictionary<string, object>();
        if (maxTokens is { } n) options["num_predict"] = n;

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = true,
            ["options"] = options
        };
        if (nativeTools) body["tools"] = AliceToolSchemas();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        {
            Content = JsonContent.Create(body)
        };
        // A stuck model must not hang the turn. This does not change how long
        // Ollama keeps the model loaded; that field is omitted on purpose.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ReplyTimeout);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                throw new HttpRequestException($"Ollama chat failed ({(int)resp.StatusCode}): {Truncate(err, 200)}");
            }

            var sb = new StringBuilder();
            var completed = false;
            List<OllamaToolCall> tools = [];
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var chunk = ParseLine(line);
                if (chunk.Error is not null) throw new HttpRequestException($"Ollama error: {Truncate(chunk.Error, 200)}");
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    sb.Append(chunk.Content);
                    onDelta?.Invoke(chunk.Content);
                }
                if (chunk.ToolCalls is { Count: > 0 }) tools = chunk.ToolCalls.ToList();
                if (chunk.Done) { completed = true; break; }
            }
            if (!completed)
                throw new HttpRequestException("Ollama chat ended before a completed reply.");
            return new OllamaCompletion(sb.ToString(), tools);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var limit = ReplyTimeout.TotalMinutes >= 1
                ? $"{ReplyTimeout.TotalMinutes:0} minutes"
                : $"{ReplyTimeout.TotalSeconds:0} seconds";
            throw new TimeoutException($"Ollama did not finish within {limit}.");
        }
    }

    /// <summary>Parse one NDJSON line from /api/chat.</summary>
    public static string ParseChunk(string line, out bool done, out string? error)
    {
        var chunk = ParseLine(line);
        done = chunk.Done;
        error = chunk.Error;
        return chunk.Content;
    }

    public readonly record struct OllamaLine(string Content, bool Done, string? Error, IReadOnlyList<OllamaToolCall>? ToolCalls);

    public static OllamaLine ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
                return new OllamaLine("", true, e.GetString() ?? "unknown", null);
            var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            var content = "";
            IReadOnlyList<OllamaToolCall>? calls = null;
            if (root.TryGetProperty("message", out var m))
            {
                if (m.TryGetProperty("content", out var c)) content = c.GetString() ?? "";
                if (m.TryGetProperty("tool_calls", out var listed) && listed.ValueKind == JsonValueKind.Array)
                    calls = ReadToolCalls(listed);
            }
            else if (root.TryGetProperty("response", out var r))
                content = r.GetString() ?? "";
            return new OllamaLine(content, done, null, calls);
        }
        catch (JsonException)
        {
            return new OllamaLine("", false, null, null);
        }
    }

    internal static bool ModelRejectsTools(string? error)
        => error is not null && error.Contains("does not support tools", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<OllamaToolCall> ReadToolCalls(JsonElement listed)
    {
        var calls = new List<OllamaToolCall>();
        foreach (var call in listed.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var fn)) continue;
            var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var args = "{}";
            if (fn.TryGetProperty("arguments", out var a))
                args = a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "{}") : a.GetRawText();
            calls.Add(new OllamaToolCall(name, args));
        }
        return calls;
    }

    private static object AliceToolSchemas() => new object[]
    {
        Tool("honcho_search", "Search your own memory about Master.", new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "What to look up" },
                ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum matches" }
            },
            ["required"] = new[] { "query" }
        }),
        Tool("honcho_save", "Save a fact about Master.", new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The fact to save" }
            },
            ["required"] = new[] { "content" }
        }),
        Tool("honcho_forget", "Remove a fact from memory. Pass content for an exact fact, or query for search terms.", new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Search terms" },
                ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact fact to delete" }
            }
        }),
        Tool("time", "The current local time.", new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>()
        }),
        Tool("search", "Search the web. Do not save the results to memory.", new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string" }
            },
            ["required"] = new[] { "query" }
        })
    };

    private static Dictionary<string, object> Tool(string name, string description, object parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters
        }
    };

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
