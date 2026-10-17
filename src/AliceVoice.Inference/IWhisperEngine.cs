using Microsoft.ML.OnnxRuntime;

namespace AliceVoice.Inference;

/// <summary>
/// Whisper speech-to-text engine running on the GPU via ONNX Runtime + DirectML.
/// The DirectML execution provider lets us pin inference to a chosen GPU.
/// </summary>
public interface IWhisperEngine
{
    /// <summary>Load the model and warm it on the inference GPU.</summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>Transcribe a 16 kHz mono PCM buffer to text.</summary>
    Task<string> TranscribeAsync(float[] pcm16k, CancellationToken ct = default);

    /// <summary>True once the model is loaded and ready.</summary>
    bool IsReady { get; }
}
