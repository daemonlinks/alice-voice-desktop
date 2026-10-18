using System.Diagnostics;
using System.Text;
using Serilog;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Piper TTS (fully local, fleet-standard) via CLI. Shells out to piper.exe
/// with the model + config, per-chunk, and plays the resulting WAV through
/// WASAPI. Mirrors the web relay's tts_stream_piper invocation exactly.
/// </summary>
public sealed class PiperTtsProvider : IVoiceProvider
{
    private readonly ITtsPlayback _playback;
    private readonly string _piperBin;
    private readonly string _voiceDir;

    public PiperTtsProvider(ITtsPlayback playback,
        string piperBin = @"C:\Users\AHC.DAEMONLINKS\AppData\Local\hermes\hermes-agent\venv\Scripts\piper.exe",
        string voiceDir = @"C:\Users\AHC.DAEMONLINKS\AppData\Local\hermes\cache\piper-voices")
    {
        _playback = playback;
        _piperBin = piperBin;
        _voiceDir = voiceDir;
    }

    public VoiceProviderKind Kind => VoiceProviderKind.Piper;
    public string DisplayName => "Piper";

    public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        var voices = new List<string>();
        try
        {
            if (Directory.Exists(_voiceDir))
                voices = Directory.GetFiles(_voiceDir, "*.onnx")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => n is not null)
                    .Cast<string>()
                    .OrderBy(x => x)
                    .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Piper voices unavailable");
        }
        return Task.FromResult<IReadOnlyList<string>>(voices);
    }

    public async Task SpeakAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var modelBase = Path.Combine(_voiceDir, string.IsNullOrEmpty(voice) ? "en_GB-southern_english_female-low" : voice);
        var modelPath = modelBase + ".onnx";
        var configPath = modelBase + ".onnx.json";
        if (!File.Exists(modelPath) || !File.Exists(configPath))
        {
            Log.Warning("Piper voice files missing for {Voice} ({Path})", voice, modelPath);
            return;
        }

        foreach (var chunk in Chunk(text, 900))
        {
            var wav = await GenerateAsync(chunk, modelPath, configPath, ct);
            if (wav is null) continue;
            var pcm = WavToPcm16(wav);
            if (pcm is not null)
                _playback.EnqueuePcm(pcm, 22050);
        }
    }

    private async Task<byte[]?> GenerateAsync(string text, string modelPath, string configPath, CancellationToken ct)
    {
        string? inPath = null, outPath = null;
        try
        {
            inPath = Path.Combine(Path.GetTempPath(), $"piper_in_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(inPath, text, Encoding.UTF8, ct);
            outPath = Path.Combine(Path.GetTempPath(), $"piper_out_{Guid.NewGuid():N}.wav");

            var psi = new ProcessStartInfo
            {
                FileName = _piperBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inPath);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(outPath);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                Log.Warning("Piper failed: {Err}", stderr.Length > 300 ? stderr[^300..] : stderr);
                return null;
            }

            var wav = await File.ReadAllBytesAsync(outPath, ct);
            Log.Information("Piper generated {Bytes} bytes", wav.Length);
            return wav;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Piper generate failed");
            return null;
        }
        finally
        {
            if (inPath is not null) TryDelete(inPath);
            if (outPath is not null) TryDelete(outPath);
        }
    }

    /// <summary>Convert a Piper WAV (16-bit PCM, mono, 22050 Hz) to Int16 PCM bytes.</summary>
    private static byte[]? WavToPcm16(byte[] wav)
    {
        try
        {
            using var ms = new MemoryStream(wav);
            using var br = new BinaryReader(ms);
            if (new string(br.ReadChars(4)) != "RIFF") return null;
            br.ReadInt32(); // size
            if (new string(br.ReadChars(4)) != "WAVE") return null;

            int fmtCode = 0, channels = 1, sampleRate = 22050, bits = 16;
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
            Log.Warning(ex, "Piper WAV parse failed");
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

        return data;
    }

    private static IEnumerable<string> Chunk(string text, int max)
    {
        for (int i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
