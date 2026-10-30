namespace AliceVoice.Core.Voice;

/// <summary>
/// Instant local TTS (Windows SAPI). No network. Implemented in the Audio
/// project (SapiLocalTts). Speech runs asynchronously off the caller's
/// thread, so the UI never blocks while Alice is talking.
/// </summary>
public interface ILocalTts
{
    /// <summary>Speak text; completes when speech finishes. Cancelling stops speech.</summary>
    Task SpeakAsync(string text, string? voiceName = null, CancellationToken ct = default);

    /// <summary>Installed, enabled voice names.</summary>
    IReadOnlyList<string> AvailableVoices();

    /// <summary>Stop any in-progress speech.</summary>
    void Stop();
}
