using System.Text.Json;
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
/// checked again right before launch. A newer release is only offered if the
/// manifest bytes carry a valid detached CMS signature (fetched from
/// <c>&lt;manifest url&gt;.p7s</c>) by a Code Signing certificate chaining to the
/// pinned daemonlinks-AHC-CA root; because the signed manifest holds the
/// package SHA-256, the package is authenticated through the hash.
/// Unsigned or invalidly signed manifests are rejected.
/// </summary>
public sealed partial class HttpAppUpdater : IAppUpdater
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _downloadDir;
    private readonly Action<string> _launch;
    private readonly string _signatureUrl;
    private readonly UpdateSignatureVerifier _verifier;
    private readonly UpdatePackagePreference _packagePreference;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HttpAppUpdater(string manifestUrl, string? downloadDir = null, HttpMessageHandler? handler = null,
        string? currentVersion = null, Action<string>? launch = null,
        UpdateSignatureVerifier? verifier = null, string? signatureUrl = null,
        UpdatePackagePreference packagePreference = UpdatePackagePreference.Windows)
    {
        _manifestUrl = manifestUrl;
        _signatureUrl = signatureUrl ?? SignatureUrlFor(manifestUrl);
        _verifier = verifier ?? UpdateSignatureVerifier.CreateDefault();
        _packagePreference = packagePreference;
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

    /// <summary>Detached signature location for a manifest: the manifest URL with ".p7s" appended to its path.</summary>
    public static string SignatureUrlFor(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var u)) return manifestUrl + ".p7s";
        var b = new UriBuilder(u);
        b.Path += ".p7s";
        return b.Uri.ToString();
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
            byte[] manifestBytes;
            using (var resp = await _http.GetAsync(_manifestUrl, ct))
            {
                resp.EnsureSuccessStatusCode();
                manifestBytes = await resp.Content.ReadAsByteArrayAsync(ct);
            }

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions);
            if (manifest is null) return null;

            var signature = await TryGetSignatureAsync(ct);
            var check = _verifier.Verify(manifestBytes, signature);
            if (!check.IsValid)
            {
                Log.Warning("Update manifest signature rejected ({Reason})", check.Reason);
                return null;
            }
            Log.Information("Update manifest signature {Reason}; signer {Subject} ({Thumbprint})",
                check.Reason, check.SignerSubject, check.SignerThumbprint);

            var release = SelectPackage(manifest, CurrentVersion, _packagePreference);
            if (release is null)
                Log.Information("Alice Voice is up to date ({Version}) or the manifest has no usable package", CurrentVersion);
            else
                Log.Information("Update available: {Latest} (installed {Current})", release.Version, CurrentVersion);
            return release;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Update check failed against {Url}", _manifestUrl);
            return null;
        }
    }

    private async Task<byte[]?> TryGetSignatureAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_signatureUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning("No update manifest signature at {Url} (HTTP {Status})", _signatureUrl, (int)resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
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

    /// <summary>
    /// Pick the package a head should offer. Callers must already have verified the
    /// detached signature over these exact bytes. Windows keeps using the top-level
    /// MSIX fields. Android prefers <c>android</c> (version, https APK URL, sha256)
    /// and otherwise falls back to the Windows fields so the UI can say the manifest
    /// has no phone package yet.
    /// </summary>
    public static ReleaseInfo? SelectPackage(string manifestJson, string currentVersion, UpdatePackagePreference preference)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, JsonOptions);
        return manifest is null ? null : SelectPackage(manifest, currentVersion, preference);
    }

    private static ReleaseInfo? SelectPackage(UpdateManifest manifest, string currentVersion, UpdatePackagePreference preference)
    {
        if (preference == UpdatePackagePreference.Android && TryPackage(manifest.Android?.Version ?? manifest.Version, manifest.Android?.DownloadUrl, manifest.Android?.Sha256, manifest, currentVersion) is { } apk
            && IsApk(apk.DownloadUrl))
            return apk;
        return TryPackage(manifest.Version, manifest.DownloadUrl, manifest.Sha256, manifest, currentVersion);
    }

    private static bool IsApk(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    private static ReleaseInfo? TryPackage(string? versionText, string? downloadUrl, string? sha, UpdateManifest manifest, string currentVersion)
    {
        var current = ParseVersion(currentVersion) ?? new Version(0, 0, 0);
        var latest = ParseVersion(versionText);
        if (latest is null || latest <= current) return null;
        if (!IsValidSha256(sha)) return null;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;
        return new ReleaseInfo(latest.ToString(3), downloadUrl!, sha!.ToLowerInvariant(), manifest.Notes, manifest.PublishedAt);
    }

    private sealed class UpdateManifest
    {
        public string Version { get; set; } = "0.0.0";
        public string DownloadUrl { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public string? Notes { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public AndroidRelease? Android { get; set; }
    }

    private sealed class AndroidRelease
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
    }
}

/// <summary>Which signed package a head should offer. The WinUI app leaves this at Windows.</summary>
public enum UpdatePackagePreference
{
    Windows,
    Android
}
