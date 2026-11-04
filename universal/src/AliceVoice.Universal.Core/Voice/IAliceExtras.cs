namespace AliceVoice.Universal.Voice;

/// <summary>
/// Remember + SOUL reload for a backend that is not the sealed WinUI
/// <c>AliceLocalBackend</c> (the phone's bridge proxy). The Windows head uses
/// the original type, which already has these methods.
/// </summary>
public interface IAliceExtras
{
    Task<bool> RememberAsync(string content, CancellationToken ct = default);
    Task ReloadSoulAsync(CancellationToken ct = default);
}
