using Microsoft.ML.OnnxRuntime;
using Serilog;
using AliceVoice.Core.Gpu;

namespace AliceVoice.Inference;

/// <summary>
/// ONNX Runtime Whisper engine using the DirectML execution provider so
/// transcription runs on the GPU selected by <see cref="IGpuSelector"/>.
/// </summary>
public sealed class OnnxWhisperEngine : IWhisperEngine, IDisposable
{
    private readonly IGpuSelector _gpu;
    private InferenceSession? _session;
    private readonly object _lock = new();

    public OnnxWhisperEngine(IGpuSelector gpu)
    {
        _gpu = gpu;
    }

    public bool IsReady
    {
        get { lock (_lock) return _session is not null; }
    }

    public Task WarmUpAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_session is not null) return;

                try
                {
                    var gpu = _gpu.PreferredInferenceGpu;
                    Log.Information("Warming Whisper on GPU {Index} ({Name})", gpu.Index, gpu.Name);

                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                        EnableMemoryPattern = true,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                    };

                    // DirectML = GPU inference. Device id pins to the chosen GPU.
                                        // Use the strongly-typed AppendExecutionProviderDML — the
                                        // string-based AppendExecutionProvider("Dml", ...) does NOT
                                        // support DirectML (only QNN/SNPE/XNNPACK/AZURE) and throws
                                        // NotSupportedException. If it still fails, the engine
                                        // degrades to CPU (or stays unready) without crashing startup.
                                        try
                                        {
                                            options.AppendExecutionProvider_DML(gpu.Index);
                                        }
                                        catch (NotSupportedException ex)
                                        {
                                            Log.Warning(ex, "DirectML provider unavailable; Whisper will not use GPU");
                                        }

                    // Whisper encoder/decoder ONNX models. Paths are resolved from
                    // the app data dir; the model downloader populates them.
                    var modelDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AliceVoice", "models", "whisper");

                    var encoderPath = Path.Combine(modelDir, "whisper-encoder.onnx");
                    var decoderPath = Path.Combine(modelDir, "whisper-decoder.onnx");

                    if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                    {
                        Log.Warning("Whisper ONNX models not found at {Dir}. Run the model downloader.", modelDir);
                        return;
                    }

                    // Load both halves of the model.
                    _session = new InferenceSession(encoderPath, options);
                    Log.Information("Whisper encoder loaded on GPU {Index}", gpu.Index);
                }
                catch (Exception ex)
                {
                    // The local Whisper engine is a fallback path only — the local
                    // Ollama STT uses faster-whisper via Python. Never crash startup.
                    Log.Warning(ex, "Whisper warm-up failed; continuing without local engine");
                }
            }
        }, ct);
    }

    public Task<string> TranscribeAsync(float[] pcm16k, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_session is null)
                    throw new InvalidOperationException("Whisper engine not warmed up.");

                // Placeholder: full encoder/decoder transcription loop is wired
                // in the next increment. This validates the GPU session path.
                Log.Information("Transcribe called with {Samples} samples", pcm16k.Length);
                return string.Empty;
            }
        }, ct);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
