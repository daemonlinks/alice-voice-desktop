using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AliceVoice.Core.Voice;

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

    /// <summary>Stream a chat completion. Returns the full reply text.</summary>
    public async Task<string> ChatAsync(string baseUrl, string model, IReadOnlyList<ChatMessage> messages,
        int? maxTokens, Action<string>? onDelta, CancellationToken ct)
    {
        var options = new Dictionary<string, object>();
        if (maxTokens is { } n) options["num_predict"] = n;

        var body = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
            options
        };

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
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var delta = ParseChunk(line, out var done, out var error);
                if (error is not null) throw new HttpRequestException($"Ollama error: {Truncate(error, 200)}");
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    onDelta?.Invoke(delta);
                }
                if (done) { completed = true; break; }
            }
            if (!completed)
                throw new HttpRequestException("Ollama chat ended before a completed reply.");
            return sb.ToString();
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
        done = false;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e)) { error = e.GetString() ?? "unknown"; done = true; return ""; }
            done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
            // /api/generate shape, tolerated.
            if (root.TryGetProperty("response", out var r)) return r.GetString() ?? "";
            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
