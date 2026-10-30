namespace AliceVoice.Core.Updates;

/// <summary>
/// Describes the latest available release as published in the version manifest.
/// <paramref name="Sha256"/> is the required lowercase hex SHA-256 of the download.
/// </summary>
public sealed record ReleaseInfo(
    string Version,
    string DownloadUrl,
    string Sha256,
    string? Notes = null,
    DateTimeOffset? PublishedAt = null);

/// <summary>A downloaded update whose SHA-256 matched the manifest.</summary>
public sealed record StagedUpdate(string Path, string Version, string Sha256);

/// <summary>
/// Checks for and applies application updates. Nothing is ever launched
/// unless its SHA-256 matches the manifest — checked after download and again
/// immediately before launch. The app never downloads or runs an update by
/// itself; it only reports that one is available.
/// </summary>
public interface IAppUpdater
{
    /// <summary>Current installed version.</summary>
    string CurrentVersion { get; }

    /// <summary>Check the manifest for a newer, verifiable version. Returns null if up to date or unverifiable.</summary>
    Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Download and verify the update. Throws (and deletes the file) if the hash does not match.</summary>
    Task<StagedUpdate> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default);

    /// <summary>Re-verify and launch a staged update. Throws if the file changed since download.</summary>
    Task ApplyUpdateAsync(StagedUpdate staged, CancellationToken ct = default);
}
