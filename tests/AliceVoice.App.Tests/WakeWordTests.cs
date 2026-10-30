using Xunit;
using AliceVoice.Core.WakeWord;

namespace AliceVoice.App.Tests;

/// <summary>Fake openWakeWord backbone: records what the pipeline asks for.</summary>
internal sealed class FakeFeatures : IWakeWordFeatureExtractor
{
    public readonly List<int> MelInputLengths = new();
    public int EmbeddingCalls;
    public int MelBins => 32;
    public int EmbeddingSize => 96;

    public float[] ComputeMel(ReadOnlySpan<float> audio, out int frames)
    {
        MelInputLengths.Add(audio.Length);
        frames = Math.Max(1, (audio.Length - 400) / 160 + 1);
        var mean = 0f;
        foreach (var s in audio) mean += s;
        mean /= audio.Length;
        var m = new float[frames * MelBins];
        Array.Fill(m, mean);
        return m;
    }

    public float[] ComputeEmbedding(ReadOnlySpan<float> melWindow)
    {
        EmbeddingCalls++;
        Assert.Equal(76 * 32, melWindow.Length);
        var e = new float[96];
        Array.Fill(e, melWindow[^1]); // newest mel value
        return e;
    }

    public void Dispose() { }
}

/// <summary>Scores = the newest embedding value (so the test controls the score with the audio).</summary>
internal sealed class FakeClassifier : IWakeWordClassifier
{
    public string Name => "fake";
    public int InputFrames => 16;
    public float Predict(ReadOnlySpan<float> features)
    {
        Assert.Equal(16 * 96, features.Length);
        return Math.Clamp(features[^1], 0f, 1f);
    }
    public void Dispose() { }
}

/// <summary>Scripted detector for session tests: returns queued scores, one per 1280 samples.</summary>
internal sealed class ScriptedDetector : IWakeWordDetector
{
    private readonly AudioFrameBuffer _chunks = new();
    public readonly Queue<float> Scores = new();
    public int Resets;
    public string ModelName => "scripted";
    public IReadOnlyList<float> Process(ReadOnlySpan<float> audio)
    {
        var o = new List<float>();
        _chunks.Push(audio, _ => o.Add(Scores.Count > 0 ? Scores.Dequeue() : 0f));
        return o;
    }
    public void Reset() { Resets++; _chunks.Clear(); }
    public void Dispose() { }
}

public class WakeWordFrameBufferTests
{
    [Fact]
    public void Rechunks50msMicBuffersInto80msFrames_InOrder_WithoutLoss()
    {
        var buf = new AudioFrameBuffer(1280);
        var frames = new List<float[]>();
        float next = 0;
        for (int b = 0; b < 8; b++) // 8 x 800 = 6400 samples = 5 frames exactly
        {
            var chunk = new float[800];
            for (int i = 0; i < chunk.Length; i++) chunk[i] = next++;
            buf.Push(chunk, frames.Add);
        }
        Assert.Equal(5, frames.Count);
        Assert.Equal(0, buf.Pending);
        var all = frames.SelectMany(f => f).ToArray();
        for (int i = 0; i < all.Length; i++) Assert.Equal(i, all[i]);
    }

    [Fact]
    public void KeepsPartialFrameUntilNextPush_AndClearDropsIt()
    {
        var buf = new AudioFrameBuffer(1280);
        var n = 0;
        buf.Push(new float[1000], _ => n++);
        Assert.Equal(0, n);
        Assert.Equal(1000, buf.Pending);
        buf.Push(new float[3000], _ => n++); // 4000 = 3 frames + 160
        Assert.Equal(3, n);
        Assert.Equal(160, buf.Pending);
        buf.Clear();
        Assert.Equal(0, buf.Pending);
        buf.Push(new float[1280], _ => n++);
        Assert.Equal(4, n);
    }

    [Fact]
    public void EmittedFramesAreCopies()
    {
        var buf = new AudioFrameBuffer(4);
        var frames = new List<float[]>();
        buf.Push(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frames.Add);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, frames[0]);
        Assert.Equal(new float[] { 5, 6, 7, 8 }, frames[1]);
    }
}

public class WakeWordPipelineTests
{
    [Fact]
    public void StreamsLikeOpenWakeWord_MelContext_OneEmbeddingPerFrame_Warmup()
    {
        var fx = new FakeFeatures();
        using var p = new OpenWakeWordPipeline(fx, new FakeClassifier());
        var initEmbeddings = fx.EmbeddingCalls;
        Assert.True(initEmbeddings > 16); // feature buffer pre-filled from 4 s of noise
        fx.MelInputLengths.Clear();

        // Constant 0.8 (int16 scale: 0.8 * 32768 ≈ 26214) -> fake score clamps to 1.
        var audio = Enumerable.Repeat(0.8f, 1280 * 10).ToArray();
        var scores = p.Process(audio);

        Assert.Equal(10, scores.Count);
        Assert.All(scores.Take(5), s => Assert.Equal(0f, s));   // reference zeroes the first 5
        Assert.All(scores.Skip(5), s => Assert.Equal(1f, s));
        Assert.Equal(10, fx.EmbeddingCalls - initEmbeddings);  // one embedding per 80 ms
        Assert.Equal(1280, fx.MelInputLengths[0]);              // first frame: no history yet
        Assert.All(fx.MelInputLengths.Skip(1), n => Assert.Equal(1280 + 480, n)); // then 480 samples of context
    }

    [Fact]
    public void ScalesToInt16AndReset_RestartsWarmup()
    {
        var fx = new FakeFeatures();
        using var p = new OpenWakeWordPipeline(fx, new FakeClassifier());
        p.Process(new float[1280 * 7]);
        Assert.Equal(7, p.FramesProcessed);
        p.Reset();
        Assert.Equal(0, p.FramesProcessed);
        var s = p.Process(Enumerable.Repeat(0.5f, 1280 * 6).ToArray());
        Assert.Equal(new[] { 0f, 0f, 0f, 0f, 0f, 1f }, s);
    }

    [Fact]
    public void PartialFramesAreHeldUntilComplete()
    {
        using var p = new OpenWakeWordPipeline(new FakeFeatures(), new FakeClassifier());
        Assert.Empty(p.Process(new float[800]));
        Assert.Single(p.Process(new float[800]));
    }
}

public class WakeWordTriggerTests
{
    [Fact]
    public void FiresAtThreshold_ThenDebouncesForRefractoryFrames()
    {
        var t = new WakeWordTrigger(threshold: 0.5f, patience: 1, refractoryFrames: 3);
        Assert.False(t.Update(0.49f));
        Assert.True(t.Update(0.5f));
        Assert.False(t.Update(0.9f)); // refractory 1
        Assert.False(t.Update(0.9f)); // 2
        Assert.False(t.Update(0.9f)); // 3
        Assert.True(t.Update(0.9f));  // armed again
        Assert.Equal(0.9f, t.PeakScore);
    }

    [Fact]
    public void PatienceNeedsConsecutiveFrames()
    {
        var t = new WakeWordTrigger(threshold: 0.5f, patience: 3, refractoryFrames: 0);
        Assert.False(t.Update(0.9f));
        Assert.False(t.Update(0.9f));
        Assert.False(t.Update(0.1f)); // run broken
        Assert.False(t.Update(0.9f));
        Assert.False(t.Update(0.9f));
        Assert.True(t.Update(0.9f));
    }

    [Fact]
    public void ResetClearsDebounce_AndThresholdIsClamped()
    {
        var t = new WakeWordTrigger(threshold: 0.5f, patience: 1, refractoryFrames: 100);
        Assert.True(t.Update(0.8f));
        Assert.False(t.Update(0.8f));
        t.Reset();
        Assert.True(t.Update(0.8f));
        t.Threshold = 5f;
        Assert.Equal(0.99f, t.Threshold);
        t.Threshold = -1f;
        Assert.Equal(0.01f, t.Threshold);
    }
}

public class EndOfSpeechDetectorTests
{
    private static float[] Tone(int ms, float amp) => Enumerable.Repeat(amp, 16 * ms).ToArray();

    [Fact]
    public void EndsAfterSilenceFollowingSpeech()
    {
        var d = new EndOfSpeechDetector(new EndOfSpeechSettings { SpeechRms = 0.02f, EndSilenceMs = 500, MinSpeechMs = 200 });
        Assert.Equal(EndOfSpeechState.WaitingForSpeech, d.Process(Tone(100, 0.001f)));
        for (int i = 0; i < 6; i++) Assert.Equal(EndOfSpeechState.Speaking, d.Process(Tone(50, 0.2f)));
        for (int i = 0; i < 9; i++) Assert.Equal(EndOfSpeechState.Speaking, d.Process(Tone(50, 0.001f)));
        Assert.Equal(EndOfSpeechState.Ended, d.Process(Tone(50, 0.001f)));
        Assert.True(d.IsDone);
    }

    [Fact]
    public void SpeechResetsTheSilenceTimer()
    {
        var d = new EndOfSpeechDetector(new EndOfSpeechSettings { SpeechRms = 0.02f, EndSilenceMs = 500, MinSpeechMs = 100 });
        d.Process(Tone(300, 0.2f));
        d.Process(Tone(400, 0.0f));
        d.Process(Tone(50, 0.2f)); // pause between words
        Assert.Equal(EndOfSpeechState.Speaking, d.Process(Tone(400, 0.0f)));
        Assert.Equal(EndOfSpeechState.Ended, d.Process(Tone(100, 0.0f)));
    }

    [Fact]
    public void TimesOutWithNoSpeech_IgnoresClicks_AndCapsLength()
    {
        var quiet = new EndOfSpeechDetector(new EndOfSpeechSettings { NoSpeechTimeoutMs = 1000, EndSilenceMs = 300, MinSpeechMs = 200 });
        quiet.Process(Tone(50, 0.5f)); // click: too short to count
        for (int i = 0; i < 5; i++) quiet.Process(Tone(100, 0f));
        Assert.Equal(EndOfSpeechState.WaitingForSpeech, quiet.State);
        for (int i = 0; i < 5; i++) quiet.Process(Tone(100, 0f));
        Assert.Equal(EndOfSpeechState.NoSpeech, quiet.State);

        var talker = new EndOfSpeechDetector(new EndOfSpeechSettings { MaxUtteranceMs = 1000 });
        for (int i = 0; i < 9; i++) Assert.Equal(EndOfSpeechState.Speaking, talker.Process(Tone(100, 0.3f)));
        Assert.Equal(EndOfSpeechState.MaxLength, talker.Process(Tone(100, 0.3f)));
    }
}

public class WakeWordSessionTests
{
    private static float[] Audio(int samples, float amp) => Enumerable.Repeat(amp, samples).ToArray();

    private static WakeWordSession NewSession(ScriptedDetector det) => new(det, new WakeWordSessionSettings
    {
        Threshold = 0.5f,
        ResumeGuardMs = 0,
        EndOfSpeech = new EndOfSpeechSettings { SpeechRms = 0.02f, EndSilenceMs = 400, MinSpeechMs = 100, NoSpeechTimeoutMs = 1000 },
    });

    [Fact]
    public void Paused_IgnoresAudio_UntilResumed()
    {
        var det = new ScriptedDetector();
        det.Scores.Enqueue(0.99f);
        using var s = NewSession(det);
        var fired = 0;
        s.WakeDetected += _ => fired++;
        s.Process(Audio(1280, 0));
        Assert.Equal(0, fired);
        Assert.Equal(WakeWordSessionState.Paused, s.State);
        s.Resume();
        s.Process(Audio(1280, 0));
        Assert.Equal(1, fired);
        Assert.Equal(WakeWordSessionState.Capturing, s.State);
    }

    [Fact]
    public void WakeThenSpeechThenSilence_DeliversUtterance_AndPauses()
    {
        var det = new ScriptedDetector();
        foreach (var x in new[] { 0.1f, 0.2f, 0.9f }) det.Scores.Enqueue(x);
        using var s = NewSession(det);
        float? wake = null;
        short[]? pcm = null;
        s.WakeDetected += v => wake = v;
        s.UtteranceReady += p => pcm = p;
        s.Resume();

        s.Process(Audio(1280 * 3, 0));
        Assert.Equal(0.9f, wake);
        for (int i = 0; i < 10; i++) s.Process(Audio(800, 0.25f)); // 500 ms of "speech"
        Assert.Null(pcm);
        for (int i = 0; i < 8; i++) s.Process(Audio(800, 0f));      // 400 ms silence
        Assert.NotNull(pcm);
        Assert.Equal(18 * 800, pcm!.Length);
        Assert.Equal((short)(0.25f * 32767), pcm[0]);
        Assert.Equal(WakeWordSessionState.Paused, s.State);     // app resumes after the reply
    }

    [Fact]
    public void NothingSaidAfterWake_AbandonsAndListensAgain()
    {
        var det = new ScriptedDetector();
        det.Scores.Enqueue(0.95f);
        using var s = NewSession(det);
        var abandoned = 0;
        s.UtteranceAbandoned += _ => abandoned++;
        s.Resume();
        s.Process(Audio(1280, 0));
        Assert.Equal(WakeWordSessionState.Capturing, s.State);
        for (int i = 0; i < 25; i++) s.Process(Audio(800, 0f)); // 1.25 s quiet
        Assert.Equal(1, abandoned);
        Assert.Equal(WakeWordSessionState.Listening, s.State);
    }

    [Fact]
    public void ResumeGuardSkipsEcho_AndThresholdIsLive()
    {
        var det = new ScriptedDetector();
        det.Scores.Enqueue(0.7f);
        using var s = new WakeWordSession(det, new WakeWordSessionSettings { Threshold = 0.8f, ResumeGuardMs = 100 });
        var fired = 0;
        s.WakeDetected += _ => fired++;
        s.Resume();
        s.Process(Audio(1600, 0)); // 100 ms guard swallowed
        Assert.Single(det.Scores); // detector never saw the guarded audio
        s.Process(Audio(1280, 0));
        Assert.Equal(0, fired);            // 0.7 < 0.8
        det.Scores.Enqueue(0.7f);
        s.Threshold = 0.6f;
        s.Process(Audio(1280, 0));
        Assert.Equal(1, fired);
    }
}

public class WakeWordModelLocatorTests
{
    private static string NewModelDir(out string fileHash)
    {
        var dir = Path.Combine(Path.GetTempPath(), "av-ww-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "mel.onnx"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, "emb.onnx"), new byte[] { 4, 5, 6 });
        File.WriteAllBytes(Path.Combine(dir, "a.onnx"), new byte[] { 7, 8, 9 });
        string H(string f) => WakeWordModelLocator.Sha256Of(Path.Combine(dir, f));
        fileHash = H("a.onnx");
        File.WriteAllText(Path.Combine(dir, WakeWordModelLocator.ManifestName), $$"""
        {
          "default": "a",
          "melspectrogram": { "file": "mel.onnx", "sha256": "{{H("mel.onnx")}}" },
          "embedding": { "file": "emb.onnx", "sha256": "{{H("emb.onnx")}}" },
          "models": [
            { "id": "a", "phrase": "hey a", "file": "a.onnx", "sha256": "{{fileHash}}", "interim": true },
            { "id": "b", "phrase": "hey b", "file": "b.onnx", "sha256": "{{fileHash}}" }
          ]
        }
        """);
        return dir;
    }

    [Fact]
    public void ResolvesDefault_VerifiesHashes_AndHonoursSelection()
    {
        var dir = NewModelDir(out _);
        try
        {
            var m = WakeWordModelLocator.TryResolve(dir, null, null, out var reason, baseDir: Path.GetTempPath());
            Assert.NotNull(m);
            Assert.Equal("", reason);
            Assert.Equal("a", m!.Id);
            Assert.Equal("hey a", m.Phrase);
            Assert.True(m.Interim);

            Assert.Null(WakeWordModelLocator.TryResolveIn(dir, "b", out reason)); // listed but file missing
            Assert.Contains("missing", reason);
            Assert.Null(WakeWordModelLocator.TryResolveIn(dir, "zzz", out reason));
            Assert.Contains("not listed", reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RefusesTamperedModel()
    {
        var dir = NewModelDir(out _);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.onnx"), new byte[] { 0, 0, 0 });
            Assert.Null(WakeWordModelLocator.TryResolveIn(dir, null, out var reason));
            Assert.Contains("SHA-256", reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SearchOrder_ConfiguredThenProgramDataThenExeThenParents()
    {
        var c = WakeWordModelLocator.CandidateDirectories(@"X:\cfg", @"Y:\pd", Path.Combine(Path.GetTempPath(), "app", "bin")).Take(4).ToList();
        Assert.Equal(@"X:\cfg", c[0]);
        Assert.Equal(Path.Combine(@"Y:\pd", "wakeword"), c[1]);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app", "bin", "models", "wakeword"), c[2]);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app", "models", "wakeword"), c[3]);
        Assert.Null(WakeWordModelLocator.TryResolve(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()), null, null, out var reason,
            baseDir: Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));
        Assert.Contains("No wake-word model", reason);
    }

    [Fact]
    public void OptionsReadWakeWordEnvOverrides()
    {
        var env = new Dictionary<string, string>
        {
            ["ALICEVOICE_WAKEWORD_DIR"] = @"D:\ww",
            ["ALICEVOICE_WAKEWORD_MODEL"] = "hey_alice",
            ["ALICEVOICE_WAKEWORD_THRESHOLD"] = "0.35",
            ["ALICEVOICE_WAKE_END_SILENCE_MS"] = "1200",
            ["ALICEVOICE_WAKE_SPEECH_RMS"] = "0.02",
        };
        var o = AliceVoice.Core.Config.AliceVoiceOptions.Load(Path.Combine(Path.GetTempPath(), "none.json"), n => env.GetValueOrDefault(n));
        Assert.Equal(@"D:\ww", o.WakeWordDir);
        Assert.Equal("hey_alice", o.WakeWordModel);
        Assert.Equal(0.35f, o.WakeWordThreshold);
        Assert.Equal(1200, o.ResolvedWakeEndSilenceMs);
        Assert.Equal(0.02f, o.ResolvedWakeSpeechRms);
        Assert.Equal(1, o.ResolvedWakeWordPatience);
    }
}
