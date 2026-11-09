using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Voice;

namespace AliceVoice.Universal.Bridge;

/// <summary>
/// Phone-side client for <see cref="BeastBridge"/>. Speaks HTTP only.
/// It never sees API keys, Honcho tokens, or the SOUL path's contents except
/// the document the editor is about to show.
/// </summary>
public sealed class BeastBridgeClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BridgeEndpoint _endpoint;
    private readonly HttpClient _http;
    private readonly bool _owns;

    public BeastBridgeClient(BeastServiceUrls urls, HttpClient? http = null)
        : this(new BridgeEndpoint(urls.Host, urls.BridgePort), http) { }

    public BeastBridgeClient(BridgeEndpoint endpoint, HttpClient? http = null)
    {
        _endpoint = endpoint;
        _owns = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public BeastServiceUrls Urls => _endpoint.Urls;

    public async Task<BridgeHealth> HealthAsync(CancellationToken ct = default)
    {
        var raw = await _http.GetStringAsync(Urls.Health, ct);
        return JsonSerializer.Deserialize<BridgeHealth>(raw, Json) ?? new BridgeHealth(false, "", false);
    }

    public async Task<string> WarmSttAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(Urls.SttWarmup, new StringContent(""), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<SttResponse>(raw, Json);
        if (!resp.IsSuccessStatusCode) throw new SttException(dto?.Error ?? $"STT bridge warmup failed ({(int)resp.StatusCode})");
        return dto?.Status ?? "Beast STT";
    }

    public async Task<SttResult> TranscribeAsync(short[] pcm, CancellationToken ct = default)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(Urls.Stt, content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<SttResponse>(raw, Json);
        if (!resp.IsSuccessStatusCode || !string.IsNullOrEmpty(dto?.Error))
            throw new SttException(dto?.Error ?? $"STT bridge failed ({(int)resp.StatusCode})");
        return new SttResult(dto?.Text ?? "", dto?.Ms ?? 0);
    }

    public async Task<SoulDocument> GetSoulAsync(CancellationToken ct = default)
    {
        var raw = await _http.GetStringAsync(Urls.Soul, ct);
        var dto = JsonSerializer.Deserialize<SoulResponse>(raw, Json) ?? throw new InvalidOperationException("Empty SOUL response");
        if (!string.IsNullOrEmpty(dto.Error)) throw new InvalidOperationException(dto.Error);
        return new SoulDocument(dto.Text ?? "", dto.Path ?? "", dto.CreatedFromTemplate);
    }

    public async Task<SoulSaveResult> SaveSoulAsync(string text, CancellationToken ct = default)
    {
        using var content = new StringContent(text, Encoding.UTF8, "text/plain");
        using var resp = await _http.PutAsync(Urls.Soul, content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<SoulSaveResponse>(raw, Json);
        if (!resp.IsSuccessStatusCode || !string.IsNullOrEmpty(dto?.Error))
            throw new InvalidOperationException(dto?.Error ?? $"SOUL save failed ({(int)resp.StatusCode})");
        return new SoulSaveResult(dto?.Path ?? "", dto?.BackupPath);
    }

    public async Task LocalConnectAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(Urls.LocalConnect, new StringContent(""), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorOf(raw) ?? $"Alice LOCAL bridge failed ({(int)resp.StatusCode})");
    }

    public async Task<string> AskLocalAsync(string prompt, AskOptions? options, Action<BackendEvent>? onEvent, CancellationToken ct = default)
    {
        try
        {
            return await AskLocalCoreAsync(prompt, options, onEvent, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Beast bridge did not answer in time.");
        }
    }

    private async Task<string> AskLocalCoreAsync(string prompt, AskOptions? options, Action<BackendEvent>? onEvent, CancellationToken ct)
    {
        var payload = new AskRequest(prompt, options?.MaxTokens, options?.StyleHint);
        using var req = new HttpRequestMessage(HttpMethod.Post, Urls.LocalAsk) { Content = JsonContent.Create(payload, options: Json) };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? final = null;
        string? error = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            BridgeEvent? evt;
            try { evt = JsonSerializer.Deserialize<BridgeEvent>(line, Json); }
            catch (JsonException) { continue; }
            if (evt?.Type is null) continue;
            onEvent?.Invoke(new BackendEvent(evt.Type, evt.Text));
            if (evt.Type == "reply_text") final = evt.Text ?? "";
            else if (evt.Type == "error") error = evt.Text ?? "error";
        }
        if (final is null && error is not null) throw new InvalidOperationException(error);
        if (final is null && !resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Alice LOCAL bridge failed ({(int)resp.StatusCode})");
        if (final is null)
            throw new InvalidOperationException("Alice LOCAL bridge returned no reply.");
        return final;
    }

    public async Task<bool> RememberAsync(string content, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.LocalRemember, new RememberRequest(content), Json, ct);
        var dto = await resp.Content.ReadFromJsonAsync<OkResponse>(Json, ct);
        return resp.IsSuccessStatusCode && dto?.Ok == true;
    }

    public async Task<TranscriptResponse> TranscriptAsync(CancellationToken ct = default)
    {
        var raw = await _http.GetStringAsync(Urls.LocalTranscript, ct);
        return JsonSerializer.Deserialize<TranscriptResponse>(raw, Json) ?? new TranscriptResponse(0, new List<SharedTurn>());
    }

    public async Task LocalCommandAsync(string type, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.LocalCommand, new CommandRequest(type), Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(ErrorOf(raw) ?? $"Command failed ({(int)resp.StatusCode})");
        }
    }

    public async Task<string> FleetSendAsync(string member, string message, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.FleetSend, new FleetSendRequest(member, message), Json, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<TextResponse>(raw, Json);
        if (!resp.IsSuccessStatusCode || !string.IsNullOrEmpty(dto?.Error))
            throw new HttpRequestException(dto?.Error ?? $"Fleet chat failed ({(int)resp.StatusCode})", null, resp.StatusCode);
        return dto?.Text ?? "";
    }

    public async Task<ReachableResponse> FleetReachableAsync(string member, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.FleetReachable, new MemberRequest(member), Json, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ReachableResponse>(raw, Json) ?? new ReachableResponse(false, false);
    }

    public async Task FleetResetAsync(string? member, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.FleetReset, new MemberRequest(member), Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<string>> PiperVoicesAsync(CancellationToken ct = default)
    {
        var raw = await _http.GetStringAsync(Urls.PiperVoices, ct);
        var dto = JsonSerializer.Deserialize<VoicesResponse>(raw, Json);
        return dto?.Voices ?? new List<string>();
    }

    public async Task<byte[]?> PiperAsync(string text, string voice, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(Urls.Piper, new PiperRequest(text, voice), Json, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(ErrorOf(raw) ?? $"Piper bridge failed ({(int)resp.StatusCode})");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        if (_owns) _http.Dispose();
    }

    private static string? ErrorOf(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
            if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}

public sealed record BridgeHealth(bool Ok, string? Stt, bool HasApiKey);
public sealed record SttResponse(string? Text, long Ms, string? Error, string? Status);
public sealed record SoulResponse(string? Text, string? Path, bool CreatedFromTemplate, string? Error);
public sealed record SoulSaveResponse(string? Path, string? BackupPath, string? Error);
public sealed record AskRequest(string? Prompt, int? MaxTokens, string? StyleHint);
public sealed record BridgeEvent(string? Type, string? Text);
public sealed record RememberRequest(string? Content);
public sealed record CommandRequest(string? Type);
public sealed record OkResponse(bool Ok, string? Error);
public sealed record FleetSendRequest(string? Member, string? Message);
public sealed record MemberRequest(string? Member);
public sealed record TextResponse(string? Text, string? Error);
public sealed record ReachableResponse(bool Ok, bool HasApiKey);
public sealed record VoicesResponse(List<string>? Voices);
public sealed record PiperRequest(string? Text, string? Voice);
public sealed record TranscriptResponse(long Epoch, List<SharedTurn> Turns);
