using System.Diagnostics;
using System.Text;
using Serilog;
using AliceVoice.Core.Audio;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Piper TTS (fully local, fleet-standard) via CLI. Shells out to piper.exe
/// (path from AliceVoiceOptions / ALICEVOICE_PIPER) with the model + config,
/// per sentence-aware chunk, and plays the resulting WAV at its real sample
/// rate. SpeakAsync completes when playback finishes; cancelling kills piper
/// and stops playback.
/// </summary>
public sealed class PiperTtsProvider : IVoiceProvider
{
    private const string DefaultVoice = "en_GB-southern_english_female-low";
    private readonly ITtsPlayback _playback;
    private readonly string _piperBin;
    private readonly string _voiceDir;

    public PiperTtsProvider(ITtsPlayback playback, AliceVoiceOptions options)
        : this(playback, options.ResolvedPiperExe, options.ResolvedPiperVoicesDir) { }

    public PiperTtsProvider(ITtsPlayback playback, string piperBin, string voiceDir)
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
        if (!File.Exists(_piperBin))
        {
            Log.Warning("Piper executable not found at {Path} (set ALICEVOICE_PIPER)", _piperBin);
            return;
        }

        var modelBase = Path.Combine(_voiceDir, string.IsNullOrEmpty(voice) ? DefaultVoice : voice);
        var modelPath = modelBase + ".onnx";
        var configPath = modelBase + ".onnx.json";
        if (!File.Exists(modelPath) || !File.Exists(configPath))
        {
            Log.Warning("Piper voice files missing for {Voice} ({Path})", voice, modelPath);
            return;
        }

        foreach (var chunk in TextChunker.Chunk(text, 900))
        {
            var wav = await GenerateAsync(chunk, modelPath, configPath, ct);
            if (wav is null) continue;
            var decoded = WavDecoder.Decode(wav);
            if (decoded is null) { Log.Warning("Piper produced an unreadable WAV ({Bytes} bytes)", wav.Length); continue; }
            _playback.EnqueuePcm(decoded.Pcm16Mono, decoded.SampleRate);
        }
        await _playback.WaitForDrainAsync(ct);
    }

    private async Task<byte[]?> GenerateAsync(string text, string modelPath, string configPath, CancellationToken ct)
    {
        string? inPath = null, outPath = null;
        Process? proc = null;
        try
        {
            inPath = Path.Combine(Path.GetTempPath(), $"piper_in_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(inPath, text, new UTF8Encoding(false), ct);
            outPath = Path.Combine(Path.GetTempPath(), $"piper_out_{Guid.NewGuid():N}.wav");

            var psi = new ProcessStartInfo
            {
                FileName = _piperBin,
                // stdout is not read, so it must not be redirected (a full pipe would hang piper).
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inPath);
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(outPath);

            proc = Process.Start(psi);
            if (proc is null) return null;
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                Log.Warning("Piper failed (exit {Code}): {Err}", proc.ExitCode, stderr.Length > 300 ? stderr[^300..] : stderr);
                return null;
            }

            var wav = await File.ReadAllBytesAsync(outPath, ct);
            Log.Information("Piper generated {Bytes} bytes", wav.Length);
            return wav;
        }
        catch (OperationCanceledException)
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Piper generate failed");
            return null;
        }
        finally
        {
            proc?.Dispose();
            if (inPath is not null) TryDelete(inPath);
            if (outPath is not null) TryDelete(outPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
