namespace AliceVoice.Core.WakeWord;

/// <summary>
/// The shared openWakeWord feature backbone: melspectrogram + Google
/// speech-embedding models. Swappable so tests can use a fake and other
/// runtimes can be plugged in.
/// </summary>
public interface IWakeWordFeatureExtractor : IDisposable
{
    /// <summary>Mel bins per frame (32 for openWakeWord).</summary>
    int MelBins { get; }

    /// <summary>Embedding size (96 for openWakeWord).</summary>
    int EmbeddingSize { get; }

    /// <summary>
    /// Melspectrogram of 16 kHz audio given as int16-valued floats (-32768..32767),
    /// already transformed the way openWakeWord does (<c>x / 10 + 2</c>).
    /// Returns <paramref name="frames"/> rows of <see cref="MelBins"/> values.
    /// </summary>
    float[] ComputeMel(ReadOnlySpan<float> audioInt16Scale, out int frames);

    /// <summary>One embedding from a 76 x <see cref="MelBins"/> mel window.</summary>
    float[] ComputeEmbedding(ReadOnlySpan<float> melWindow);
}

/// <summary>
/// A wake-word classifier on top of the shared features (e.g. hey_jarvis,
/// hey_alice). Swappable: any model taking [1, InputFrames, EmbeddingSize]
/// and returning one 0..1 score fits.
/// </summary>
public interface IWakeWordClassifier : IDisposable
{
    string Name { get; }

    /// <summary>How many embedding frames the model looks at (16 for the stock models).</summary>
    int InputFrames { get; }

    /// <summary>Score 0..1 for the last <see cref="InputFrames"/> embeddings (row-major).</summary>
    float Predict(ReadOnlySpan<float> features);
}

/// <summary>Streaming wake-word scorer: audio in, one score per 80 ms frame out.</summary>
public interface IWakeWordDetector : IDisposable
{
    /// <summary>Name of the active model (e.g. hey_jarvis).</summary>
    string ModelName { get; }

    /// <summary>Feed 16 kHz mono audio as -1..1 floats; returns the scores of the 80 ms frames completed by it.</summary>
    IReadOnlyList<float> Process(ReadOnlySpan<float> audio);

    /// <summary>Clear all streaming state (as after a fresh start).</summary>
    void Reset();
}
