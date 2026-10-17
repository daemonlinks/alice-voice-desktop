namespace AliceVoice.Core.Updates;

/// <summary>
/// Describes the latest available release as published in the version manifest.
/// </summary>
public sealed record ReleaseInfo(
    string Version,
    string DownloadUrl,
    string? Notes = null,
    DateTimeOffset? PublishedAt = null);

/// <summary>
/// Checks for and applies application updates. The app self-updates so the
/// source and binaries are always retained and current.
/// </summary>
public interface IAppUpdater
{
    /// <summary>Current installed version.</summary>
    string CurrentVersion { get; }

    /// <summary>Check the manifest for a newer version. Returns null if up to date.</summary>
    Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Download and stage the update. Returns the staged file path.</summary>
    Task<string> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default);

    /// <summary>Apply a staged update (launch installer / replace binaries).</summary>
    Task ApplyUpdateAsync(string stagedPath, CancellationToken ct = default);
}
