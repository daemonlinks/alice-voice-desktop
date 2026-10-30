using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using AliceVoice.Core.Audio;
using AliceVoice.Core.WakeWord;
using AliceVoice.Inference.WakeWord;

namespace AliceVoice.App.Tests;

/// <summary>
/// Numerical parity of the .NET openWakeWord pipeline against the Python
/// openwakeword package. tests/fixtures/wakeword/parity_*.json holds the
/// per-80 ms scores produced by tools/wakeword-parity/oww_reference.py for the
/// WAVs next to it (SAPI speech padded with 1.5 s of low noise).
/// </summary>
public class WakeWordParityTests(ITestOutputHelper output)
{
    /// <summary>The reference pre-fills 16 feature frames from random noise; compare after they scroll out.</summary>
    private const int SkipFrames = 16;
    private const float Tolerance = 0.005f;

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "models", "wakeword", WakeWordModelLocator.ManifestName)))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with models/wakeword not found");
    }

    internal static float[] LoadWav(string path)
    {
        var wav = WavDecoder.Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException($"not a WAV: {path}");
        Assert.Equal(16000, wav.SampleRate);
        var f = new float[wav.Pcm16Mono.Length / 2];
        for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(wav.Pcm16Mono, i * 2) / 32768f;
        return f;
    }

    public static IEnumerable<object[]> ParityFiles()
    {
        var root = RepoRoot();
        foreach (var p in Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "wakeword"), "parity_*.json"))
            yield return new object[] { Path.GetFileName(p) };
    }

    [Theory]
    [MemberData(nameof(ParityFiles))]
    public void DotNetScoresMatchPythonOpenWakeWord(string parityFile)
    {
        var root = RepoRoot();
        var fixtures = Path.Combine(root, "tests", "fixtures", "wakeword");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, parityFile)));
        var modelId = doc.RootElement.GetProperty("model").GetString()!;
        var model = WakeWordModelLocator.TryResolveIn(Path.Combine(root, "models", "wakeword"), modelId, out var reason);
        Assert.True(model is not null, reason);
        var phrase = model!.Phrase.Replace(' ', '_');

        var worst = 0f;
        foreach (var file in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            var expected = file.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            using var det = OnnxWakeWordFactory.CreateDetector(model);
            var actual = det.Process(LoadWav(Path.Combine(fixtures, file.Name))).ToArray();

            Assert.Equal(expected.Length, actual.Length);
            var maxDiff = 0f;
            for (int i = SkipFrames; i < expected.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(expected[i] - actual[i]));
            worst = Math.Max(worst, maxDiff);
            var pyMax = expected.Skip(SkipFrames).Max();
            var netMax = actual.Skip(SkipFrames).Max();
            output.WriteLine($"{modelId} {file.Name}: frames={actual.Length} maxScore py={pyMax:0.000000} net={netMax:0.000000} maxAbsDiff={maxDiff:0.000000}");

            Assert.True(maxDiff <= Tolerance, $"{file.Name}: max |py - net| = {maxDiff}");
            var positive = file.Name.StartsWith(phrase, StringComparison.OrdinalIgnoreCase);
            if (positive)
            {
                Assert.True(pyMax >= model.Entry.Threshold, $"{file.Name}: Python max {pyMax} below threshold");
                Assert.True(netMax >= model.Entry.Threshold, $"{file.Name}: .NET max {netMax} below threshold");
                Assert.True(Fires(actual, model.Entry), $"{file.Name}: no detection with patience {model.Entry.Patience}");
            }
            else
            {
                Assert.True(pyMax < model.Entry.Threshold, $"{file.Name}: Python max {pyMax} triggered");
                Assert.True(netMax < model.Entry.Threshold, $"{file.Name}: .NET max {netMax} triggered");
                Assert.False(Fires(actual, model.Entry), $"{file.Name}: false detection");
            }
        }
        output.WriteLine($"{modelId}: worst maxAbsDiff over all files = {worst:0.000000}");
    }

    /// <summary>Runs the scores through the same trigger the app uses (manifest threshold + patience).</summary>
    private static bool Fires(float[] scores, WakeWordModelEntry entry)
    {
        var trigger = new WakeWordTrigger(entry.Threshold, entry.Patience);
        return scores.Skip(SkipFrames).Any(trigger.Update);
    }

    [Fact]
    public void ShippedModelsMatchManifestHashes()
    {
        var dir = Path.Combine(RepoRoot(), "models", "wakeword");
        var manifest = WakeWordModelLocator.LoadManifest(dir);
        Assert.False(string.IsNullOrEmpty(manifest.Default));
        foreach (var m in manifest.Models)
            Assert.True(WakeWordModelLocator.TryResolveIn(dir, m.Id, out var reason) is not null, reason);
    }
}
