namespace AliceVoice.Audio;

/// <summary>
/// Plays generated TTS audio (mono Int16 PCM) through the default output
/// device. Chunks are queued without loss; <see cref="WaitForDrainAsync"/>
/// completes when everything queued has been played.
/// </summary>
public interface ITtsPlayback : IAsyncDisposable
{
    /// <summary>Queue a mono Int16 PCM chunk for playback.</summary>
    void EnqueuePcm(byte[] pcmBytes, int sampleRate);

    /// <summary>Wait until queued audio has finished playing. Cancelling stops playback.</summary>
    Task WaitForDrainAsync(CancellationToken ct = default);

    /// <summary>Stop and clear any queued audio. Later chunks start playback again.</summary>
    void Stop();
}
