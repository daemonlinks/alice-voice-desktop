using System.Text.Json;

namespace AliceVoice.Universal.Media;

/// <summary>What kind of media a display command carries. Only images render today; video and others can be switched on later behind <see cref="MediaSettings.AllowedKinds"/>.</summary>
public enum MediaKind { Image, Video }

/// <summary>Where a picture may come from.</summary>
public enum MediaSources
{
    /// <summary>Default. Only files under the allowed roots (served to phones by the Tailscale-only bridge).</summary>
    AllowedRootsOnly,
    /// <summary>Allowed roots, plus https:// URLs.</summary>
    AllowedRootsAndHttps
}

/// <summary>
/// The four media settings, kept tight: on/off, allowed sources, max size, default
/// display duration. Plus the allowed roots, which are the SAME list the Alice Voice
/// relay uses (<c>SHARE_ROOTS</c>). Environment wins over the settings file; the
/// settings file wins over the relay's config.json; the relay's built-in list is last.
/// </summary>
public sealed record MediaSettings
{
    /// <summary>The relay's hard ceiling (MEDIA_MAX_BYTES, 40 MB). The setting may lower it, never raise it.</summary>
    public const long CeilingBytes = 40L * 1024 * 1024;
    public const double DefaultDurationSeconds = 20;
    public const string RelayConfigDefault = @"C:\Users\AHC.DAEMONLINKS\ahc-the-beast\voice\config.json";

    public static readonly IReadOnlyList<string> RelayDefaultRoots = new[]
    {
        @"D:\Private\shoots\Alice_Voice\Made_for_Master",
        @"D:\Private\Model Directory\Alice_Voice",
        @"C:\Users\AHC.DAEMONLINKS\ahc-the-beast\voice\Mirrored",
        Path.Combine(Path.GetTempPath(), "voice-share"),
    };

    public bool Enabled { get; init; } = true;
    public MediaSources Sources { get; init; } = MediaSources.AllowedRootsOnly;
    public long MaxBytes { get; init; } = CeilingBytes;
    public double DefaultDurationS { get; init; } = DefaultDurationSeconds;
    public IReadOnlyList<string> Roots { get; init; } = RelayDefaultRoots;
    /// <summary>Images only for now. Adding <see cref="MediaKind.Video"/> here is the future switch; the renderer must support it first.</summary>
    public IReadOnlySet<MediaKind> AllowedKinds { get; init; } = new HashSet<MediaKind> { MediaKind.Image };

    public bool AllowHttps => Sources == MediaSources.AllowedRootsAndHttps;

    public static MediaSettings Default { get; } = new();

    /// <summary>
    /// Keys: <c>ALICEVOICE_MEDIA</c> (on/off), <c>ALICEVOICE_MEDIA_SOURCES</c> (<c>roots</c> | <c>roots+https</c>),
    /// <c>ALICEVOICE_MEDIA_MAX_MB</c>, <c>ALICEVOICE_MEDIA_DURATION_S</c>, <c>ALICEVOICE_SHARE_ROOTS</c> (<c>;</c>-separated).
    /// The settings file may carry <c>Media</c>, <c>MediaSources</c>, <c>MediaMaxMb</c>, <c>MediaDurationS</c>, <c>ShareRoots</c>.
    /// </summary>
    public static MediaSettings Load(string? settingsFile, Func<string, string?>? env = null, string? relayConfig = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var file = ReadFile(settingsFile);
        string? Pick(string envName, string? fromFile) => env(envName) is { Length: > 0 } v ? v : string.IsNullOrWhiteSpace(fromFile) ? null : fromFile;

        var s = new MediaSettings();
        if (Pick("ALICEVOICE_MEDIA", file?.Media) is { } on) s = s with { Enabled = !IsOff(on) };
        if (Pick("ALICEVOICE_MEDIA_SOURCES", file?.MediaSources) is { } src)
            s = s with { Sources = src.Trim().ToLowerInvariant() is "roots+https" or "https" or "roots,https" ? MediaSources.AllowedRootsAndHttps : MediaSources.AllowedRootsOnly };
        if (Pick("ALICEVOICE_MEDIA_MAX_MB", file?.MediaMaxMb?.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } mb
            && double.TryParse(mb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0)
            s = s with { MaxBytes = Math.Min(CeilingBytes, (long)(m * 1024 * 1024)) };
        if (Pick("ALICEVOICE_MEDIA_DURATION_S", file?.MediaDurationS?.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } d
            && double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs is > 0 and <= 3600)
            s = s with { DefaultDurationS = secs };

        var roots = SplitRoots(env("ALICEVOICE_SHARE_ROOTS"));
        if (roots.Count == 0 && file?.ShareRoots is { Count: > 0 } fr) roots = fr.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (roots.Count == 0) roots = ReadRelayRoots(relayConfig ?? env("ALICEVOICE_RELAY_CONFIG") ?? RelayConfigDefault);
        if (roots.Count > 0) s = s with { Roots = roots };
        return s;
    }

    private static bool IsOff(string v) => v.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no";

    internal static List<string> SplitRoots(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? new List<string>() : raw.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Only the SHARE_ROOTS key is read from the relay's config.json. Nothing else in that file is touched or logged.</summary>
    internal static List<string> ReadRelayRoots(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new List<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("SHARE_ROOTS", out var r)) return new List<string>();
            if (r.ValueKind == JsonValueKind.Array) return r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0).ToList();
            if (r.ValueKind == JsonValueKind.String) return SplitRoots(r.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning("Could not read SHARE_ROOTS from the relay config ({Type}); using the relay's built-in list", ex.GetType().Name);
        }
        return new List<string>();
    }

    private static FileKeys? ReadFile(string? settingsFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile)) return null;
            return JsonSerializer.Deserialize<FileKeys>(File.ReadAllText(settingsFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning("Could not read media keys from {File}", settingsFile);
            return null;
        }
    }

    private sealed class FileKeys
    {
        public string? Media { get; set; }
        public string? MediaSources { get; set; }
        public double? MediaMaxMb { get; set; }
        public double? MediaDurationS { get; set; }
        public List<string>? ShareRoots { get; set; }
    }
}
