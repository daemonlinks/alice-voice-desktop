using System.Net.Http.Json;
using System.Text.Json;
using Serilog;

namespace AliceVoice.Core.Updates;

/// <summary>
/// Self-updater that reads a version manifest (JSON) from a configured URL,
/// compares against the installed version, and downloads the new build.
/// The manifest is the single source of truth for "what is current".
/// </summary>
public sealed class HttpAppUpdater : IAppUpdater
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _downloadDir;

    public HttpAppUpdater(string manifestUrl, string? downloadDir = null)
    {
        _manifestUrl = manifestUrl;
        _downloadDir = downloadDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AliceVoice", "updates");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string CurrentVersion =>
        typeof(HttpAppUpdater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_manifestUrl, ct);
            resp.EnsureSuccessStatusCode();

            var manifest = await resp.Content.ReadFromJsonAsync<UpdateManifest>(ct);
            if (manifest is null) return null;

            var current = new Version(CurrentVersion);
            var latest = new Version(manifest.Version);

            if (latest <= current)
            {
                Log.Information("Alice Voice is up to date ({Version})", CurrentVersion);
                return null;
            }

            Log.Information("Update available: {Latest} (installed {Current})",
                manifest.Version, CurrentVersion);
            return new ReleaseInfo(manifest.Version, manifest.DownloadUrl, manifest.Notes, manifest.PublishedAt);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed against {Url}", _manifestUrl);
            return null;
        }
    }

    public async Task<string> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_downloadDir);
        var fileName = Path.GetFileName(new Uri(release.DownloadUrl).AbsolutePath);
        if (string.IsNullOrEmpty(fileName)) fileName = $"alice-voice-{release.Version}.msix";
        var target = Path.Combine(_downloadDir, fileName);

        Log.Information("Downloading update {Version} from {Url}", release.Version, release.DownloadUrl);
        using var resp = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var fs = File.Create(target);
        await resp.Content.CopyToAsync(fs, ct);

        Log.Information("Update downloaded to {Path}", target);
        return target;
    }

    public Task ApplyUpdateAsync(string stagedPath, CancellationToken ct = default)
    {
        // Launch the installer / MSIX package. For an unpackaged app this
        // launches the new installer which replaces the binaries on next start.
        Log.Information("Applying update from {Path}", stagedPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = stagedPath,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = "0.0.0";
        public string DownloadUrl { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
