namespace AliceVoice.Universal.Audio;

/// <summary>
/// 16 kHz mono float frames (-1..1), the same contract as the WinUI
/// <c>IMicrophoneCapture</c>. Defined here so the session does not reference
/// the Windows-only audio project.
/// </summary>
public interface IPcmMicrophone : IAsyncDisposable
{
    event Action<float[]>? AudioFrameCaptured;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
