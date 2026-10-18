using System.Net.Http;
using System.Text;
using Serilog;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// AllTalk TTS via direct HTTP — no relay. POSTs text to the AllTalk server's
/// /api/tts-generate endpoint (form-encoded), downloads the generated WAV, and
/// plays it through WASAPI. AllTalk returns IEEE-float 32-bit WAV (fmt=3);
/// this converts it to Int16 PCM for playback.
/// </summary>
public sealed class AllTalkTtsProvider : IVoiceProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly ITtsPlayback _playback;
    private readonly string _baseUrl;

    public AllTalkTtsProvider(ITtsPlayback playback, string baseUrl = "http://127.0.0.1:7851")
    {
        _playback = playback;
        _baseUrl = baseUrl;
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

        // AllTalk rejects text_input over 1000 chars — chunk it.
        foreach (var chunk in Chunk(text, 900))
        {
            var wav = await GenerateAsync(chunk, voice, ct);
            if (wav is null) continue;
            var pcm = WavToPcm16(wav);
            if (pcm is not null)
                _playback.EnqueuePcm(pcm, 24000);
        }
    }

    private async Task<byte[]?> GenerateAsync(string text, string voice, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text_input"] = text,
            ["text_filtering"] = "none",
            ["character_voice_gen"] = string.IsNullOrEmpty(voice) ? "female_07.wav" : voice,
            ["narrator_enabled"] = "false",
            ["narrator_voice_gen"] = string.IsNullOrEmpty(voice) ? "female_07.wav" : voice,
            ["text_not_inside"] = "character",
            ["language"] = "en",
            ["output_file_name"] = $"alicevoice_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
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

        var data = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!data.RootElement.TryGetProperty("output_file_url", out var urlEl))
        {
            Log.Warning("AllTalk response missing output_file_url");
            return null;
        }

        var audioUrl = urlEl.GetString();
        if (string.IsNullOrEmpty(audioUrl)) return null;

        var wav = await _http.GetByteArrayAsync(audioUrl, ct);
        Log.Information("AllTalk generated {Bytes} bytes", wav.Length);
        return wav;
    }

    /// <summary>Convert an IEEE-float (fmt=3) or PCM (fmt=1) WAV to Int16 PCM bytes.</summary>
    private static byte[]? WavToPcm16(byte[] wav)
    {
        try
        {
            using var ms = new MemoryStream(wav);
            using var br = new BinaryReader(ms);
            if (new string(br.ReadChars(4)) != "RIFF") return null;
            br.ReadInt32(); // size
            if (new string(br.ReadChars(4)) != "WAVE") return null;

            int fmtCode = 0, channels = 1, sampleRate = 24000, bits = 16;
            while (ms.Position < ms.Length)
            {
                var id = new string(br.ReadChars(4));
                var size = br.ReadInt32();
                if (id == "fmt ")
                {
                    fmtCode = br.ReadInt16();
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadInt16(); // block align
                    bits = br.ReadInt16();
                    if (size > 16) br.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    var data = br.ReadBytes(size);
                    return ConvertToPcm16(data, fmtCode, bits);
                }
                else
                {
                    br.ReadBytes(size);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WAV parse failed");
        }
        return null;
    }

    private static byte[] ConvertToPcm16(byte[] data, int fmtCode, int bits)
    {
        if (fmtCode == 1 && bits == 16) return data; // already Int16 PCM

        if (fmtCode == 3) // IEEE float
        {
            var floats = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, floats, 0, data.Length);
            var outBytes = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                var v = Math.Clamp(floats[i], -1f, 1f);
                var s = (short)(v * 32767f);
                outBytes[i * 2] = (byte)(s & 0xFF);
                outBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return outBytes;
        }

        // Fallback: assume 16-bit PCM.
        return data;
    }

    private static IEnumerable<string> Chunk(string text, int max)
    {
        for (int i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }
}
