using System.Net.Http;
using System.Text.Json;
using AliceVoice.Core.Audio;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using Serilog;

namespace AliceVoice.Universal.Audio;

/// <summary>
/// AllTalk over HTTP, same request as the WinUI provider (form post to
/// /api/tts-generate, then the WAV at output_file_url). Used by the Android
/// head so it can speak the Beast's AllTalk voices. The Windows head keeps
/// the original provider in AliceVoice.Audio.
/// </summary>
public sealed class AllTalkVoiceProvider : IVoiceProvider, IDisposable
{
    private const string DefaultVoice = "female_07.wav";
    private readonly HttpClient _http;
    private readonly IPcmPlayer _playback;
    private readonly string _baseUrl;
    private readonly bool _ownsClient;

    public AllTalkVoiceProvider(IPcmPlayer playback, AliceVoiceOptions options, HttpClient? http = null)
        : this(playback, options.ResolvedAllTalkUrl, http) { }

    public AllTalkVoiceProvider(IPcmPlayer playback, string baseUrl, HttpClient? http = null)
    {
        _playback = playback;
        _baseUrl = baseUrl.TrimEnd('/');
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    public VoiceProviderKind Kind => VoiceProviderKind.AllTalk;
    public string DisplayName => "AllTalk";

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_baseUrl}/api/voices", ct);
            using var doc = JsonDocument.Parse(json);
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

        using var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!data.RootElement.TryGetProperty("output_file_url", out var urlEl))
        {
            Log.Warning("AllTalk response missing output_file_url");
            return null;
        }

        var audioUrl = urlEl.GetString();
        if (string.IsNullOrEmpty(audioUrl)) return null;
        if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out _))
            audioUrl = _baseUrl + (audioUrl.StartsWith('/') ? "" : "/") + audioUrl;

        var wav = await _http.GetByteArrayAsync(audioUrl, ct);
        Log.Information("AllTalk generated {Bytes} bytes", wav.Length);
        return wav;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
