namespace AliceVoice.Core.Stt;

/// <summary>Result of one transcription.</summary>
public sealed record SttResult(string Text, long ElapsedMs);

/// <summary>
/// Speech-to-text for one complete utterance (16 kHz mono Int16 PCM).
/// Implementations keep the model warm between calls.
/// </summary>
public interface ISttClient : IAsyncDisposable
{
    /// <summary>Start the engine and load the model (idempotent).</summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>Transcribe one utterance. Throws <see cref="SttException"/> on failure.</summary>
    Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default);

    bool IsReady { get; }

    /// <summary>Human-readable engine status, e.g. "faster-whisper large-v3 · GPU 1".</summary>
    string Status { get; }
}

public sealed class SttException(string message, Exception? inner = null) : Exception(message, inner);
