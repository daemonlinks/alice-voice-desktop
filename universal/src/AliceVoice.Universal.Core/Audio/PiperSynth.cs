using System.Diagnostics;
using System.Text;
using AliceVoice.Core.Audio;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using Serilog;

namespace AliceVoice.Universal.Audio;

public interface IPiperSynth
{
    IReadOnlyList<string> ListVoices();
    Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct = default);
}

/// <summary>
/// Piper CLI, same arguments as the WinUI provider. Returns one WAV (chunks
/// concatenated) so the phone bridge can hand the bytes back. Playback stays
/// on the device that asked.
/// </summary>
public sealed class PiperSynth : IPiperSynth
{
    private const string DefaultVoice = "en_GB-southern_english_female-low";
    private readonly string _piperBin;
    private readonly string _voiceDir;

    public PiperSynth(AliceVoiceOptions options) : this(options.ResolvedPiperExe, options.ResolvedPiperVoicesDir) { }

    public PiperSynth(string piperBin, string voiceDir)
    {
        _piperBin = piperBin;
        _voiceDir = voiceDir;
    }

    public IReadOnlyList<string> ListVoices()
    {
        try
        {
            if (!Directory.Exists(_voiceDir)) return Array.Empty<string>();
            return Directory.GetFiles(_voiceDir, "*.onnx")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Piper voices unavailable");
            return Array.Empty<string>();
        }
    }

    public async Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!File.Exists(_piperBin))
        {
            Log.Warning("Piper executable not found at {Path} (set ALICEVOICE_PIPER)", _piperBin);
            return null;
        }

        var modelBase = Path.Combine(_voiceDir, string.IsNullOrEmpty(voice) ? DefaultVoice : voice);
        var modelPath = modelBase + ".onnx";
        var configPath = modelBase + ".onnx.json";
        if (!File.Exists(modelPath) || !File.Exists(configPath))
        {
            Log.Warning("Piper voice files missing for {Voice} ({Path})", voice, modelPath);
            return null;
        }

        var pcm = new List<byte>();
        int rate = 0;
        foreach (var chunk in TextChunker.Chunk(text, 900))
        {
            var wav = await GenerateAsync(chunk, modelPath, configPath, ct);
            if (wav is null) continue;
            var decoded = WavDecoder.Decode(wav);
            if (decoded is null) continue;
            rate = decoded.SampleRate;
            pcm.AddRange(decoded.Pcm16Mono);
        }
        if (pcm.Count == 0 || rate <= 0) return null;
        return WavEncoder.EncodePcm16Mono(pcm.ToArray(), rate);
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
            TryDelete(inPath);
            TryDelete(outPath);
        }
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Android Piper: ask the Beast bridge, then play the WAV locally. A missing bridge is an error the user can see.</summary>
public sealed class RemotePiperProvider : IVoiceProvider
{
    private readonly IPcmPlayer _playback;
    private readonly Bridge.BeastBridgeClient? _bridge;

    public RemotePiperProvider(IPcmPlayer playback, Bridge.BeastBridgeClient? bridge)
    {
        _playback = playback;
        _bridge = bridge;
    }

    public VoiceProviderKind Kind => VoiceProviderKind.Piper;
    public string DisplayName => "Piper";

    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        if (_bridge is null) return Array.Empty<string>();
        try { return await _bridge.PiperVoicesAsync(ct); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Piper voices unavailable");
            return Array.Empty<string>();
        }
    }

    public async Task SpeakAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_bridge is null)
            throw new InvalidOperationException("Piper on this phone runs on the Beast. Set ALICEVOICE_BEAST_HOST and ALICEVOICE_BRIDGE_BIND on the PC.");
        var wav = await _bridge.PiperAsync(text, voice, ct);
        if (wav is null) return;
        var decoded = WavDecoder.Decode(wav);
        if (decoded is null) { Log.Warning("Piper bridge returned an unreadable WAV ({Bytes} bytes)", wav.Length); return; }
        _playback.EnqueuePcm(decoded.Pcm16Mono, decoded.SampleRate);
        await _playback.WaitForDrainAsync(ct);
    }
}
