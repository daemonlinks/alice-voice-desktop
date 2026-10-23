using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace AliceVoice.Core.Chat;

/// <summary>
/// Raised when a fleet member replies.
/// </summary>
public sealed record ChatReply(string Member, string Text);

/// <summary>
/// Fast fleet chat: POSTs text to the Hermes API server's OpenAI-compatible
/// /p/&lt;profile&gt;/v1/chat/completions endpoint. Plain HTTP, no WebSocket, no
/// relay — the fastest way to talk to any fleet member.
/// </summary>
public interface IFleetChat
{
    /// <summary>Send a message to a fleet member and return their reply.</summary>
    Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default);

    /// <summary>Check whether a member is reachable (has a key + is served).</summary>
    Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default);
}

public sealed class HermesApiFleetChat : IFleetChat
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _sharedKey;

    public HermesApiFleetChat(string baseUrl = "http://127.0.0.1:8643", string? sharedKey = null)
    {
        _baseUrl = baseUrl;
        _sharedKey = sharedKey ?? ApiKeyResolver.Resolve();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default)
    {
        var key = member.ApiKey ?? _sharedKey;
        var url = $"{_baseUrl}/p/{member.Profile}/v1/chat/completions";

        var body = new
        {
            model = member.Profile,
            messages = new[]
            {
                new { role = "user", content = message }
            },
            stream = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning("Fleet chat to {Member} failed: {Code} {Body}", member.Name, (int)resp.StatusCode, raw);
            throw new HttpRequestException($"Chat to {member.Name} failed ({resp.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var reply = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        Log.Information("Fleet chat {Member} replied ({Chars} chars)", member.Name, reply.Length);
        return reply;
    }

    public async Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default)
    {
        var key = member.ApiKey ?? _sharedKey;
        var url = $"{_baseUrl}/p/{member.Profile}/v1/models";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reachability check failed for {Member}", member.Name);
            return false;
        }
    }
}
