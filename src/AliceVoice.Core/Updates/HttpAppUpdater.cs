using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Serilog;

namespace AliceVoice.Core.Updates;

/// <summary>
/// Self-updater that reads a version manifest (JSON) from a configured URL and
/// compares it with the installed version. A release is only offered if the
/// manifest carries a valid SHA-256 and an https download URL; downloads go to
/// a .partial file, are hashed, and are deleted on mismatch; the hash is
/// checked again right before launch. Authenticode signature checking is a
/// follow-up (no signing certificate exists yet).
/// </summary>
public sealed partial class HttpAppUpdater : IAppUpdater
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _downloadDir;
    private readonly Action<string> _launch;

    public HttpAppUpdater(string manifestUrl, string? downloadDir = null, HttpMessageHandler? handler = null,
        string? currentVersion = null, Action<string>? launch = null)
    {
        _manifestUrl = manifestUrl;
        _downloadDir = downloadDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AliceVoice", "updates");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        CurrentVersion = currentVersion ?? DetectVersion();
        _launch = launch ?? (path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        }));
    }

    /// <summary>App version (from Directory.Build.props), read from the entry assembly.</summary>
    public string CurrentVersion { get; }

    private static string DetectVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(HttpAppUpdater).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseVersion(info)?.ToString(3) ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Tolerant parse: accepts "1.2", "v1.2.3", "1.2.3-beta+abc". Returns null if unparseable.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = VersionRx().Match(s.Trim());
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    [GeneratedRegex(@"^[vV]?(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?")]
    private static partial Regex VersionRx();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Rx();

    public static bool IsValidSha256(string? s) => s is not null && Sha256Rx().IsMatch(s);

    public async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_manifestUrl, ct);
            resp.EnsureSuccessStatusCode();

            var manifest = await resp.Content.ReadFromJsonAsync<UpdateManifest>(ct);
            if (manifest is null) return null;

            var current = ParseVersion(CurrentVersion) ?? new Version(0, 0, 0);
            var latest = ParseVersion(manifest.Version);
            if (latest is null)
            {
                Log.Warning("Update manifest has an unreadable version {Version}", manifest.Version);
                return null;
            }
            if (latest <= current)
            {
                Log.Information("Alice Voice is up to date ({Version})", CurrentVersion);
                return null;
            }
            if (!IsValidSha256(manifest.Sha256))
            {
                Log.Warning("Update {Version} ignored: manifest has no valid sha256", manifest.Version);
                return null;
            }
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                Log.Warning("Update {Version} ignored: download URL is not https", manifest.Version);
                return null;
            }

            Log.Information("Update available: {Latest} (installed {Current})", manifest.Version, CurrentVersion);
            return new ReleaseInfo(manifest.Version, manifest.DownloadUrl, manifest.Sha256!.ToLowerInvariant(),
                manifest.Notes, manifest.PublishedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Update check failed against {Url}", _manifestUrl);
            return null;
        }
    }

    public async Task<StagedUpdate> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (!IsValidSha256(release.Sha256)) throw new InvalidOperationException("Release has no valid SHA-256; refusing to download.");
        var uri = new Uri(release.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Update download URL must be https.");

        Directory.CreateDirectory(_downloadDir);
        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            fileName = $"alice-voice-{release.Version}.msix";
        var target = Path.Combine(_downloadDir, fileName);
        var partial = target + ".partial";

        Log.Information("Downloading update {Version} from {Url}", release.Version, release.DownloadUrl);
        try
        {
            using (var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(partial);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var actual = await HashFileAsync(partial, ct);
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update hash mismatch: expected {release.Sha256}, got {actual}. The download was deleted.");

            File.Move(partial, target, overwrite: true);
            Log.Information("Update {Version} downloaded and verified (sha256 {Hash})", release.Version, actual);
            return new StagedUpdate(target, release.Version, actual);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public async Task ApplyUpdateAsync(StagedUpdate staged, CancellationToken ct = default)
    {
        if (!File.Exists(staged.Path)) throw new FileNotFoundException("Staged update not found", staged.Path);
        var actual = await HashFileAsync(staged.Path, ct);
        if (!IsValidSha256(staged.Sha256) || !string.Equals(actual, staged.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged.Path);
            throw new InvalidDataException("Staged update changed after verification; it was deleted and not launched.");
        }
        Log.Information("Launching verified update {Version} from {Path}", staged.Version, staged.Path);
        _launch(staged.Path);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = "0.0.0";
        public string DownloadUrl { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public string? Notes { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
