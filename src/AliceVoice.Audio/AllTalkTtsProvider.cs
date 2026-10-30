using System.Net.Http;
using Serilog;
using AliceVoice.Core.Audio;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// AllTalk TTS via direct HTTP — no relay. POSTs text to the AllTalk server's
/// /api/tts-generate endpoint (form-encoded), downloads the generated WAV, and
/// plays it at the WAV's real sample rate. Long text is split at sentence
/// boundaries (AllTalk rejects &gt;1000 chars). SpeakAsync completes when the
/// audio has finished playing; cancelling stops playback.
/// </summary>
public sealed class AllTalkTtsProvider : IVoiceProvider, IDisposable
{
    private const string DefaultVoice = "female_07.wav";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly ITtsPlayback _playback;
    private readonly string _baseUrl;

    public AllTalkTtsProvider(ITtsPlayback playback, AliceVoiceOptions options)
        : this(playback, options.ResolvedAllTalkUrl) { }

    public AllTalkTtsProvider(ITtsPlayback playback, string baseUrl)
    {
        _playback = playback;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public VoiceProviderKind Kind => VoiceProviderKind.AllTalk;
    public string DisplayName => "AllTalk";

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}/api/voices", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("voices", out var arr))
                return arr.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AllTalk voices unavailable");
        }
        return new List<string>();
    }

    public async Task SpeakAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Generate chunk N+1 while chunk N plays; wait for playback at the end.
        foreach (var chunk in TextChunker.Chunk(text, 900))
        {
            var wav = await GenerateAsync(chunk, voice, ct);
            if (wav is null) continue;
            var decoded = WavDecoder.Decode(wav);
            if (decoded is null) { Log.Warning("AllTalk returned an unreadable WAV ({Bytes} bytes)", wav.Length); continue; }
            _playback.EnqueuePcm(decoded.Pcm16Mono, decoded.SampleRate);
        }
        await _playback.WaitForDrainAsync(ct);
    }

    private async Task<byte[]?> GenerateAsync(string text, string voice, CancellationToken ct)
    {
        var v = string.IsNullOrEmpty(voice) ? DefaultVoice : voice;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text_input"] = text,
            ["text_filtering"] = "none",
            ["character_voice_gen"] = v,
            ["narrator_enabled"] = "false",
            ["narrator_voice_gen"] = v,
            ["text_not_inside"] = "character",
            ["language"] = "en",
            // Unique per chunk (the old per-second name collided between chunks).
            ["output_file_name"] = $"alicevoice_{Guid.NewGuid():N}",
            ["output_file_timestamp"] = "false",
            ["autoplay"] = "false",
            ["autoplay_volume"] = "0.1"
        });

        using var resp = await _http.PostAsync($"{_baseUrl}/api/tts-generate", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning("AllTalk generate failed: {Code}", (int)resp.StatusCode);
            return null;
        }

        using var data = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!data.RootElement.TryGetProperty("output_file_url", out var urlEl))
        {
            Log.Warning("AllTalk response missing output_file_url");
            return null;
        }

        var audioUrl = urlEl.GetString();
        if (string.IsNullOrEmpty(audioUrl)) return null;
        // AllTalk v2 returns a server-relative path ("/audio/x.wav").
        if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out _))
            audioUrl = _baseUrl + (audioUrl.StartsWith('/') ? "" : "/") + audioUrl;

        var wav = await _http.GetByteArrayAsync(audioUrl, ct);
        Log.Information("AllTalk generated {Bytes} bytes", wav.Length);
        return wav;
    }

    public void Dispose() => _http.Dispose();
}
