namespace AliceVoice.Universal.Audio;

/// <summary>Plays Int16 mono PCM. Same contract as the WinUI <c>ITtsPlayback</c>.</summary>
public interface IPcmPlayer : IAsyncDisposable
{
    void EnqueuePcm(byte[] pcmBytes, int sampleRate);
    Task WaitForDrainAsync(CancellationToken ct = default);
    void Stop();
}
