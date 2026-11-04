using AliceVoice.Core.Voice;

namespace AliceVoice.Universal.Voice;

/// <summary>
/// The only way the editor reads or writes SOUL. On Windows this is the local
/// <see cref="SoulStore"/> (ProgramData, backups, agent-path refusal). On Android
/// it is the Beast bridge, which calls that same store — the phone never writes
/// a second SOUL file.
/// </summary>
public interface ISoulGateway
{
    Task<SoulDocument> LoadAsync(CancellationToken ct = default);
    Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default);
}

/// <summary>WinUI-compatible local store. Does not reload the backend; the session does that after a save.</summary>
public sealed class LocalSoulGateway : ISoulGateway
{
    private readonly SoulStore _store;
    public LocalSoulGateway(SoulStore store) => _store = store;
    public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);
    public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => _store.SaveAsync(text, ct);
}

/// <summary>Phone with no Beast configured. Refuses to create a local SOUL.</summary>
public sealed class RefusingSoulGateway : ISoulGateway
{
    public const string Message =
        "The SOUL editor on this phone writes only the Beast's %ProgramData%\\AliceVoice\\alice_local\\SOUL.md, through the bridge. " +
        "Set ALICEVOICE_BEAST_HOST to the Beast (Tailscale) and run App U there with ALICEVOICE_BRIDGE_BIND.";

    public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => throw new InvalidOperationException(Message);
    public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => throw new InvalidOperationException(Message);
}
