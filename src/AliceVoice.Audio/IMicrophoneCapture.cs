namespace AliceVoice.Audio;

/// <summary>
/// Low-latency microphone capture via NAudio WaveIn on the default device. Produces 16 kHz mono PCM
/// float buffers ready for Whisper transcription.
/// </summary>
public interface IMicrophoneCapture : IAsyncDisposable
{
    /// <summary>Raised with each captured 16 kHz mono PCM float buffer.</summary>
    event Action<float[]>? AudioFrameCaptured;

    /// <summary>Start capturing from the default input device.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop capturing.</summary>
    Task StopAsync();
}
