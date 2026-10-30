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
                    // Placeholder engine (not used for STT — faster-whisper is).
                    // Check for models BEFORE touching any GPU so a missing model
                    // never creates a DirectML device.
                    var modelDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AliceVoice", "models", "whisper");
                    var encoderPath = Path.Combine(modelDir, "whisper-encoder.onnx");
                    var decoderPath = Path.Combine(modelDir, "whisper-decoder.onnx");
                    if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
                    {
                        Log.Information("ONNX Whisper models not present at {Dir}; ONNX engine idle (faster-whisper handles STT)", modelDir);
                        return;
                    }

                    // Never the render GPU 0; null = no usable voice GPU.
                    var gpu = _gpu.PreferredInferenceGpu;
                    if (gpu is null)
                    {
                        Log.Warning("No voice GPU available; ONNX Whisper engine stays idle");
                        return;
                    }
                    Log.Information("Warming ONNX Whisper on GPU {Index} ({Name})", gpu.Index, gpu.Name);

                    var options = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                        EnableMemoryPattern = true,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                    };

                    // NOTE: the DirectML device id is the DXGI adapter ordinal,
                    // which is not guaranteed to match nvidia-smi's index.
                    try
                    {
                        options.AppendExecutionProvider_DML(gpu.Index);
                    }
                    catch (NotSupportedException ex)
                    {
                        Log.Warning(ex, "DirectML provider unavailable; Whisper will not use GPU");
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
