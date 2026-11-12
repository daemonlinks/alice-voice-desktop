using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AliceVoice.Core.Voice;
using Serilog;

namespace AliceVoice.Core.Chat;

/// <summary>
/// Raised when a fleet member replies.
/// </summary>
public sealed record ChatReply(string Member, string Text);

/// <summary>
/// Fast fleet chat: POSTs text to the Hermes API server's OpenAI-compatible
/// /p/&lt;profile&gt;/v1/chat/completions endpoint. Plain HTTP, no WebSocket, no
/// relay. Keeps a separate rolling conversation history per member.
/// </summary>
public interface IFleetChat
{
    /// <summary>Send a message (with that member's history) and return their reply.</summary>
    Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default);

    /// <summary>Check whether a member is reachable (has a key + is served).</summary>
    Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default);

    /// <summary>Clear one member's history, or everyone's when null.</summary>
    void ResetConversation(FleetMember? member = null);

    /// <summary>True when an API_SERVER_KEY was found (value never exposed).</summary>
    bool HasApiKey { get; }
}

public sealed class HermesApiFleetChat : IFleetChat, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _sharedKey;
    private readonly bool _sharedKeyExplicit;
    private readonly ConcurrentDictionary<string, ConversationHistory> _histories = new();

    public HermesApiFleetChat(string baseUrl, string? sharedKey = null, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _sharedKeyExplicit = !string.IsNullOrEmpty(sharedKey);
        _sharedKey = sharedKey ?? ApiKeyResolver.Resolve();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public bool HasApiKey => !string.IsNullOrEmpty(_sharedKey);

    /// <summary>
    /// The api_server to use for this member: its own host when it lives off this
    /// host (WSL/VPS), otherwise the session's configured server.
    /// </summary>
    private string BaseFor(FleetMember m) =>
        string.IsNullOrEmpty(m.BaseUrl) ? _baseUrl : m.BaseUrl!.TrimEnd('/');

    private ConversationHistory History(FleetMember m) => _histories.GetOrAdd(m.Name, _ => new ConversationHistory(20));

    public void ResetConversation(FleetMember? member = null)
    {
        if (member is null) _histories.Clear();
        else _histories.TryRemove(member.Name, out _);
    }

    public async Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default)
    {
        var history = History(member);
        var messages = history.Snapshot()
            .Select(m => new { role = m.Role, content = m.Content })
            .Append(new { role = "user", content = message })
            .ToArray();

        var body = new { model = member.Profile, messages, stream = false };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseFor(member)}/p/{member.Profile}/v1/chat/completions");
        AddAuth(req, member);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Truncate: error bodies can be large and may echo request content.
            var snippet = OllamaChatClient.Truncate(raw.Trim(), 200);
            Log.Warning("Fleet chat to {Member} failed: {Code} {Body}", member.Name, (int)resp.StatusCode, snippet);
            throw new HttpRequestException($"Chat to {member.Name} failed ({(int)resp.StatusCode} {resp.StatusCode}): {snippet}", null, resp.StatusCode);
        }

        var reply = ParseReply(raw);
        history.AddTurn(message, reply);
        Log.Information("Fleet chat {Member} replied ({Chars} chars)", member.Name, reply.Length);
        return reply;
    }

    internal static string ParseReply(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    /// <summary>Throws with the HTTP status if the member is not served / the key is rejected.</summary>
    public async Task EnsureReachableAsync(FleetMember member, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseFor(member)}/p/{member.Profile}/v1/models");
        AddAuth(req, member);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{member.DisplayName} not reachable at {BaseFor(member)} ({(int)resp.StatusCode} {resp.StatusCode}{(string.IsNullOrEmpty(KeyFor(member)) ? ", no API_SERVER_KEY found" : "")})",
                null, resp.StatusCode);
    }

    public async Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default)
    {
        try
        {
            await EnsureReachableAsync(member, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warning("Reachability check failed for {Member}: {Message}", member.Name, ex.Message);
            return false;
        }
    }

    private void AddAuth(HttpRequestMessage req, FleetMember member)
    {
        var key = KeyFor(member);
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>
    /// The key to present for this member: its explicit key, else the injected
    /// shared key, else that profile's OWN key on the host that serves it.
    /// </summary>
    private string KeyFor(FleetMember member) =>
        member.ApiKey ?? (_sharedKeyExplicit ? _sharedKey : ApiKeyResolver.Resolve(member.Profile, member.BaseUrl));

    public void Dispose() => _http.Dispose();
}
