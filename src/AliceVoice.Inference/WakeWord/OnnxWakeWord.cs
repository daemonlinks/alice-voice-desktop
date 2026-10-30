using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using AliceVoice.Core.WakeWord;

namespace AliceVoice.Inference.WakeWord;

internal static class WakeWordOnnx
{
    /// <summary>CPU only, one thread per model: these models are tiny and must never touch a GPU.</summary>
    public static InferenceSession Open(string path)
    {
        using var opts = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return new InferenceSession(path, opts); // default = CPU execution provider
    }

    public static float[] Run(InferenceSession s, string input, float[] data, int[] shape, out int[] outShape)
    {
        var tensor = new DenseTensor<float>(data, shape);
        using var results = s.Run(new[] { NamedOnnxValue.CreateFromTensor(input, tensor) });
        var t = results.First().AsTensor<float>();
        outShape = t.Dimensions.ToArray();
        return t.ToArray();
    }
}

/// <summary>openWakeWord melspectrogram.onnx + embedding_model.onnx on the CPU.</summary>
public sealed class OnnxWakeWordFeatureExtractor : IWakeWordFeatureExtractor
{
    private readonly InferenceSession _mel;
    private readonly InferenceSession _emb;
    private readonly string _melInput;
    private readonly string _embInput;

    public OnnxWakeWordFeatureExtractor(string melspectrogramPath, string embeddingPath)
    {
        _mel = WakeWordOnnx.Open(melspectrogramPath);
        try
        {
            _emb = WakeWordOnnx.Open(embeddingPath);
        }
        catch
        {
            _mel.Dispose();
            throw;
        }
        _melInput = _mel.InputMetadata.Keys.First();
        _embInput = _emb.InputMetadata.Keys.First();
    }

    public int MelBins => 32;
    public int EmbeddingSize => 96;

    public float[] ComputeMel(ReadOnlySpan<float> audioInt16Scale, out int frames)
    {
        var raw = WakeWordOnnx.Run(_mel, _melInput, audioInt16Scale.ToArray(), [1, audioInt16Scale.Length], out _);
        frames = raw.Length / MelBins;
        for (int i = 0; i < raw.Length; i++) raw[i] = raw[i] / 10f + 2f; // openWakeWord's transform
        return raw;
    }

    public float[] ComputeEmbedding(ReadOnlySpan<float> melWindow)
    {
        if (melWindow.Length != OpenWakeWordPipeline.MelWindow * MelBins)
            throw new ArgumentException("Mel window must be 76 x 32", nameof(melWindow));
        return WakeWordOnnx.Run(_emb, _embInput, melWindow.ToArray(), [1, OpenWakeWordPipeline.MelWindow, MelBins, 1], out _);
    }

    public void Dispose()
    {
        _mel.Dispose();
        _emb.Dispose();
    }
}

/// <summary>An openWakeWord classifier (.onnx taking [1, frames, 96]) on the CPU.</summary>
public sealed class OnnxWakeWordClassifier : IWakeWordClassifier
{
    private readonly InferenceSession _s;
    private readonly string _input;

    public OnnxWakeWordClassifier(string path, string name)
    {
        _s = WakeWordOnnx.Open(path);
        Name = name;
        var meta = _s.InputMetadata.First();
        _input = meta.Key;
        var dims = meta.Value.Dimensions;
        InputFrames = dims.Length >= 2 && dims[1] > 0 ? dims[1] : 16;
    }

    public string Name { get; }
    public int InputFrames { get; }

    public float Predict(ReadOnlySpan<float> features)
    {
        var size = features.Length / InputFrames;
        var output = WakeWordOnnx.Run(_s, _input, features.ToArray(), [1, InputFrames, size], out _);
        return output.Length > 0 ? output[0] : 0f;
    }

    public void Dispose() => _s.Dispose();
}

/// <summary>Builds a ready wake-word detector from a verified model set.</summary>
public static class OnnxWakeWordFactory
{
    public static OpenWakeWordPipeline CreateDetector(WakeWordModelInfo model)
    {
        var features = new OnnxWakeWordFeatureExtractor(model.MelspectrogramPath, model.EmbeddingPath);
        try
        {
            var classifier = new OnnxWakeWordClassifier(model.ClassifierPath, model.Id);
            return new OpenWakeWordPipeline(features, classifier);
        }
        catch
        {
            features.Dispose();
            throw;
        }
    }
}
