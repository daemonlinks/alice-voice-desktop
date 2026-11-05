using AliceVoice.Core.Config;
using AliceVoice.Core.WakeWord;
using AliceVoice.Universal.Wake;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AliceVoice.AppU;

/// <summary>Copies the verified wake models out of the app package, then scores them on ONNX Runtime CPU.</summary>
public sealed class AndroidWakeSource : IWakeSource
{
    private static readonly string[] Files =
    {
        "wakeword-models.json", "melspectrogram.onnx", "embedding_model.onnx",
        "hey_alice_v0.1.onnx", "hey_jarvis_v0.1.onnx", "NOTICE.md"
    };

    private readonly AliceVoiceOptions _options;
    public AndroidWakeSource(AliceVoiceOptions options) => _options = options;

    public async Task<IWakeController> OpenAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(FileSystem.AppDataDirectory, "wakeword");
        Directory.CreateDirectory(dir);
        foreach (var name in Files)
        {
            var dest = Path.Combine(dir, name);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync("wakeword/" + name);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct);
            }
            catch (Exception ex)
            {
                return new UnavailableWakeController($"Wake-word file {name} was not in the app package ({ex.Message}).");
            }
        }

        _options.WakeWordDir ??= dir;
        return WakeControllerFactory.Create(_options, AndroidOnnxWake.CreateDetector, dir);
    }
}

/// <summary>Same CPU-only openWakeWord runner as AliceVoice.Inference, linked against the Android ONNX Runtime build.</summary>
public static class AndroidOnnxWake
{
    public static IWakeWordDetector CreateDetector(WakeWordModelInfo model)
    {
        var features = new FeatureExtractor(model.MelspectrogramPath, model.EmbeddingPath);
        try
        {
            var classifier = new Classifier(model.ClassifierPath, model.Id);
            return new OpenWakeWordPipeline(features, classifier);
        }
        catch
        {
            features.Dispose();
            throw;
        }
    }

    private static InferenceSession Open(string path)
    {
        using var opts = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        return new InferenceSession(path, opts);
    }

    private static float[] Run(InferenceSession session, string input, float[] data, int[] shape)
    {
        var tensor = new DenseTensor<float>(data, shape);
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, tensor) });
        return results.First().AsTensor<float>().ToArray();
    }

    private sealed class FeatureExtractor : IWakeWordFeatureExtractor
    {
        private readonly InferenceSession _mel;
        private readonly InferenceSession _emb;
        private readonly string _melInput;
        private readonly string _embInput;

        public FeatureExtractor(string melPath, string embPath)
        {
            _mel = Open(melPath);
            try { _emb = Open(embPath); }
            catch { _mel.Dispose(); throw; }
            _melInput = _mel.InputMetadata.Keys.First();
            _embInput = _emb.InputMetadata.Keys.First();
        }

        public int MelBins => 32;
        public int EmbeddingSize => 96;

        public float[] ComputeMel(ReadOnlySpan<float> audioInt16Scale, out int frames)
        {
            var raw = Run(_mel, _melInput, audioInt16Scale.ToArray(), new[] { 1, audioInt16Scale.Length });
            frames = raw.Length / MelBins;
            for (var i = 0; i < raw.Length; i++) raw[i] = raw[i] / 10f + 2f;
            return raw;
        }

        public float[] ComputeEmbedding(ReadOnlySpan<float> melWindow)
        {
            if (melWindow.Length != OpenWakeWordPipeline.MelWindow * MelBins)
                throw new ArgumentException("Mel window must be 76 x 32", nameof(melWindow));
            return Run(_emb, _embInput, melWindow.ToArray(), new[] { 1, OpenWakeWordPipeline.MelWindow, MelBins, 1 });
        }

        public void Dispose() { _mel.Dispose(); _emb.Dispose(); }
    }

    private sealed class Classifier : IWakeWordClassifier
    {
        private readonly InferenceSession _session;
        private readonly string _input;
        public Classifier(string path, string name)
        {
            _session = Open(path);
            Name = name;
            var meta = _session.InputMetadata.First();
            _input = meta.Key;
            var dims = meta.Value.Dimensions;
            InputFrames = dims.Length >= 2 && dims[1] > 0 ? dims[1] : 16;
        }

        public string Name { get; }
        public int InputFrames { get; }
        public float Predict(ReadOnlySpan<float> features)
        {
            var size = features.Length / InputFrames;
            var output = Run(_session, _input, features.ToArray(), new[] { 1, InputFrames, size });
            return output.Length > 0 ? output[0] : 0f;
        }
        public void Dispose() => _session.Dispose();
    }
}
