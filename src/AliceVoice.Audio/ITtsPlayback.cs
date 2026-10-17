namespace AliceVoice.Audio;

/// <summary>
/// Plays TTS audio chunks received from the relay. The relay streams raw
/// Int16 PCM (22050 Hz AllTalk / 16000 Hz Edge); this plays them via WASAPI.
/// </summary>
public interface ITtsPlayback : IAsyncDisposable
{
    /// <summary>Queue a raw Int16 PCM chunk for playback.</summary>
    void EnqueuePcm(byte[] pcmBytes, int sampleRate);

    /// <summary>Stop and clear any queued audio.</summary>
    void Stop();
}
