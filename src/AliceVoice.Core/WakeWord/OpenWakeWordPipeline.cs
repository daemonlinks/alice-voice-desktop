namespace AliceVoice.Core.WakeWord;

/// <summary>
/// openWakeWord's streaming pipeline (openwakeword 0.6.0 <c>AudioFeatures</c> +
/// <c>Model.predict</c>), re-implemented in .NET:
/// 16 kHz audio in 80 ms frames (1280 samples) -> melspectrogram over the frame
/// plus 480 samples of context -> mel buffer (starts as 76 rows of ones) ->
/// one embedding per frame from the last 76 mel rows (8-row step) -> feature
/// buffer (pre-filled with embeddings of 4 s of low-level noise, max 120 rows)
/// -> classifier on the last N embeddings -> score. The first 5 scores after a
/// reset are forced to 0, as in the reference.
/// </summary>
public sealed class OpenWakeWordPipeline : IWakeWordDetector
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 1280;          // 80 ms
    public const int MelContextSamples = 160 * 3;  // extra samples the reference feeds the mel model
    public const int MelWindow = 76;
    public const int MelStep = 8;
    public const int MelBufferMax = 10 * 97;
    public const int FeatureBufferMax = 120;
    public const int WarmupFrames = 5;

    private readonly IWakeWordFeatureExtractor _features;
    private readonly IWakeWordClassifier _classifier;
    private readonly bool _ownsModels;
    private readonly AudioFrameBuffer _chunker = new(FrameSamples);
    private readonly float[] _raw = new float[FrameSamples + MelContextSamples];
    private int _rawCount;
    private readonly List<float[]> _mel = new();
    private readonly List<float[]> _featureRows = new();
    private float[][]? _initialFeatures;
    private int _predictions;

    public OpenWakeWordPipeline(IWakeWordFeatureExtractor features, IWakeWordClassifier classifier, bool ownsModels = true)
    {
        _features = features;
        _classifier = classifier;
        _ownsModels = ownsModels;
        Reset();
    }

    public string ModelName => _classifier.Name;

    /// <summary>Number of 80 ms frames scored since the last reset.</summary>
    public int FramesProcessed => _predictions;

    public void Reset()
    {
        _chunker.Clear();
        _rawCount = 0;
        _predictions = 0;
        _mel.Clear();
        for (int i = 0; i < MelWindow; i++)
        {
            var row = new float[_features.MelBins];
            Array.Fill(row, 1f);
            _mel.Add(row);
        }
        _initialFeatures ??= InitialFeatures();
        _featureRows.Clear();
        foreach (var r in _initialFeatures) _featureRows.Add(r);
    }

    /// <summary>
    /// The reference pre-fills its feature buffer with embeddings of 4 s of
    /// uniform random int16 noise in [-1000, 1000); same here, with a fixed seed
    /// so runs are reproducible. Only the first 16 frames after a reset see it.
    /// </summary>
    private float[][] InitialFeatures()
    {
        var rng = new Random(1337);
        var noise = new float[SampleRate * 4];
        for (int i = 0; i < noise.Length; i++) noise[i] = rng.Next(-1000, 1000);
        var mel = _features.ComputeMel(noise, out var frames);
        var bins = _features.MelBins;
        var rows = new List<float[]>();
        for (int start = 0; start + MelWindow <= frames; start += MelStep)
            rows.Add(_features.ComputeEmbedding(mel.AsSpan(start * bins, MelWindow * bins)));
        return rows.ToArray();
    }

    public IReadOnlyList<float> Process(ReadOnlySpan<float> audio)
    {
        var scores = new List<float>();
        // The reference works on int16 samples; the mic gives int16/32768.
        var scaled = new float[audio.Length];
        for (int i = 0; i < audio.Length; i++)
            scaled[i] = MathF.Round(Math.Clamp(audio[i] * 32768f, -32768f, 32767f));
        _chunker.Push(scaled, f => scores.Add(ProcessFrameInt16Scale(f)));
        return scores;
    }

    /// <summary>Score exactly one 80 ms frame of int16-valued floats.</summary>
    public float ProcessFrameInt16Scale(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples) throw new ArgumentException($"Frame must be {FrameSamples} samples", nameof(frame));

        // Raw buffer: keep the last 1280 + 480 samples (the reference keeps 10 s
        // but only ever reads the newest n + 480).
        var keep = Math.Min(_rawCount, MelContextSamples);
        if (keep > 0) Array.Copy(_raw, _rawCount - keep, _raw, 0, keep);
        frame.CopyTo(_raw.AsSpan(keep));
        _rawCount = keep + FrameSamples;

        var mel = _features.ComputeMel(_raw.AsSpan(0, _rawCount), out var frames);
        var bins = _features.MelBins;
        for (int f = 0; f < frames; f++) _mel.Add(mel.AsSpan(f * bins, bins).ToArray());
        if (_mel.Count > MelBufferMax) _mel.RemoveRange(0, _mel.Count - MelBufferMax);

        if (_mel.Count >= MelWindow)
        {
            var window = new float[MelWindow * bins];
            for (int r = 0; r < MelWindow; r++)
                _mel[_mel.Count - MelWindow + r].CopyTo(window, r * bins);
            _featureRows.Add(_features.ComputeEmbedding(window));
            if (_featureRows.Count > FeatureBufferMax) _featureRows.RemoveRange(0, _featureRows.Count - FeatureBufferMax);
        }

        var n = _classifier.InputFrames;
        var size = _features.EmbeddingSize;
        var input = new float[n * size];
        var first = Math.Max(0, _featureRows.Count - n);
        var offset = (n - (_featureRows.Count - first)) * size; // left-pad if (theoretically) short
        for (int i = first; i < _featureRows.Count; i++)
            _featureRows[i].CopyTo(input, offset + (i - first) * size);

        var score = _classifier.Predict(input);
        _predictions++;
        return _predictions <= WarmupFrames ? 0f : score;
    }

    public void Dispose()
    {
        if (!_ownsModels) return;
        _classifier.Dispose();
        _features.Dispose();
    }
}
