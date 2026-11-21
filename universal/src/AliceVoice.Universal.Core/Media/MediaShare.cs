using System.Text;

namespace AliceVoice.Universal.Media;

/// <summary>The result of checking one local file against the share rules. <see cref="Error"/> uses the relay's fixed strings.</summary>
public sealed record MediaCheck(bool Ok, string? Error, string? FullPath = null, long Bytes = 0, MediaKind Kind = MediaKind.Image, string? ContentType = null)
{
    public static MediaCheck Fail(string error) => new(false, error);
}

/// <summary>
/// The relay's share rules (relay.py <c>_share_media</c>, contract §1/§3b), ported:
/// absolute path only, resolved, must be a file, must sit under one of the allowed
/// roots (a containment check on the fully resolved path, which is what kills
/// <c>..</c> tricks), extension allow-list, size cap. Symlinks are followed to their
/// final target and the TARGET must also be inside a root. Error strings match the
/// relay: <c>not a file</c>, <c>path not allowed</c>, <c>unsupported type</c>, <c>too large</c>.
/// </summary>
public sealed class MediaShare
{
    public const string NotAFile = "not a file";
    public const string PathNotAllowed = "path not allowed";
    public const string UnsupportedType = "unsupported type";
    public const string TooLarge = "too large";

    /// <summary>Only from <see cref="ResolveName"/>: a name matching more than one file is refused, never guessed at.</summary>
    public const string AmbiguousName = "that name matches more than one file";

    private static readonly Dictionary<string, (MediaKind Kind, string Type)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = (MediaKind.Image, "image/png"),
        [".jpg"] = (MediaKind.Image, "image/jpeg"),
        [".jpeg"] = (MediaKind.Image, "image/jpeg"),
        [".webp"] = (MediaKind.Image, "image/webp"),
        [".gif"] = (MediaKind.Image, "image/gif"),
        [".mp4"] = (MediaKind.Video, "video/mp4"),
        [".webm"] = (MediaKind.Video, "video/webm"),
    };

    private readonly IReadOnlyList<string> _roots;
    private readonly long _maxBytes;
    private readonly StringComparison _cmp;

    public MediaShare(IEnumerable<string> roots, long maxBytes = MediaSettings.CeilingBytes, bool? caseInsensitive = null)
    {
        _maxBytes = Math.Min(maxBytes, MediaSettings.CeilingBytes);
        _cmp = (caseInsensitive ?? OperatingSystem.IsWindows()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _roots = roots.Select(NormalizeRoot).Where(r => r is not null).Select(r => r!).ToList();
    }

    public MediaShare(MediaSettings settings) : this(settings.Roots, settings.MaxBytes) { }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>The relay's media extension allow-list (images and short video).</summary>
    public static bool TryKindOf(string pathOrUrl, out MediaKind kind, out string contentType)
    {
        var ext = Path.GetExtension(StripQuery(pathOrUrl));
        if (Types.TryGetValue(ext, out var t)) { kind = t.Kind; contentType = t.Type; return true; }
        kind = MediaKind.Image;
        contentType = "";
        return false;
    }

    public MediaCheck Validate(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return MediaCheck.Fail(NotAFile);
        var raw = src.Trim();
        // Device / extended paths and NTFS alternate streams are never a share path.
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal) || raw.StartsWith(@"\\.\", StringComparison.Ordinal) || raw.StartsWith("//?/", StringComparison.Ordinal))
            return MediaCheck.Fail(PathNotAllowed);
        if (raw.IndexOf('\0') >= 0) return MediaCheck.Fail(PathNotAllowed);
        if (!Path.IsPathFullyQualified(raw)) return MediaCheck.Fail(PathNotAllowed);
        if (OperatingSystem.IsWindows() && raw.IndexOf(':', 2) >= 0) return MediaCheck.Fail(PathNotAllowed);

        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return MediaCheck.Fail(NotAFile); }

        if (!IsUnderRoot(full)) return MediaCheck.Fail(PathNotAllowed);

        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists) return MediaCheck.Fail(NotAFile);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !target.Exists || target is not FileInfo finalFile) return MediaCheck.Fail(NotAFile);
                if (!IsUnderRoot(Path.GetFullPath(finalFile.FullName))) return MediaCheck.Fail(PathNotAllowed);
                info = finalFile;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return MediaCheck.Fail(NotAFile); }

        if (!TryKindOf(full, out var kind, out var type)) return MediaCheck.Fail(UnsupportedType);
        if (info.Length > _maxBytes) return MediaCheck.Fail(TooLarge);
        return new MediaCheck(true, null, full, info.Length, kind, type);
    }

    /// <summary>
    /// Up to <paramref name="n"/> showable names (filename without extension), first root first,
    /// sorted within a root and de-duplicated. This mirrors the web page's
    /// <c>_picture_names</c> exactly, so a local model is offered the same names in both
    /// products - and <see cref="ResolveName"/> turns one back into a file.
    /// </summary>
    public IReadOnlyList<string> Catalogue(int n = 12)
    {
        var cap = Math.Clamp(n, 1, 40);
        var names = new List<string>(cap);
        foreach (var root in _roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var byName = _cmp == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var file in files.OrderBy(f => Path.GetFileName(f), byName))
            {
                if (!TryKindOf(file, out _, out _)) continue;
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length == 0 || names.Contains(stem, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(stem);
                if (names.Count >= cap) return names;
            }
        }
        return names;
    }

    /// <summary>The web page's hint, word for word, so a refusal reads the same in both products.</summary>
    public string CatalogueHint(int n = 8)
    {
        var names = Catalogue(n);
        return names.Count == 0 ? "no pictures found in the allowed folders" : "name one of these: " + string.Join(", ", names);
    }

    /// <summary>
    /// Resolve a bare picture name to a file under the allowed roots. Alice LOCAL can name a
    /// picture but cannot be expected to produce an absolute path, which is the local leg's way in -
    /// the web page's <c>gui_list_pictures</c> names work the same way. The extension is optional,
    /// the scan is capped by depth, a name matching more than one file is refused rather than
    /// guessed at, and the winner still goes through <see cref="Validate"/> so the roots, the type
    /// allow-list and the size cap all still apply.
    /// </summary>
    public MediaCheck ResolveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return MediaCheck.Fail(NotAFile);
        var wanted = name.Trim();
        if (wanted.IndexOf('\0') >= 0 || wanted.IndexOfAny(new[] { '/', '\\' }) >= 0) return MediaCheck.Fail(PathNotAllowed);
        if (wanted is "." or "..") return MediaCheck.Fail(PathNotAllowed);

        var hasExt = Types.ContainsKey(Path.GetExtension(wanted));
        var stem = hasExt ? Path.GetFileNameWithoutExtension(wanted) : wanted;
        if (stem.Length == 0) return MediaCheck.Fail(NotAFile);

        string? found = null;
        foreach (var root in _roots)
        {
            foreach (var dir in Walk(root, MaxNameSearchDepth))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var match = hasExt
                        ? string.Equals(fileName, wanted, _cmp)
                        : string.Equals(Path.GetFileNameWithoutExtension(fileName), stem, _cmp);
                    if (!match) continue;
                    if (found is not null) return MediaCheck.Fail(AmbiguousName);
                    found = file;
                }
            }
        }
        return found is null ? MediaCheck.Fail(NotAFile) : Validate(found);
    }

    /// <summary>A picture catalogue lives at or near the top of a root: a full recursive scan of a big share would be a stall.</summary>
    private const int MaxNameSearchDepth = 3;

    private static IEnumerable<string> Walk(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            yield return dir;
            if (depth >= maxDepth) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs) queue.Enqueue((sub, depth + 1));
        }
    }

    /// <summary>A bridge id for a local path: base64url of its UTF-8 bytes. The bridge re-validates on every request, so an id grants nothing by itself.</summary>
    public static string ToId(string path)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryFromId(string? id, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(id) || id.Length > 4096) return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        var b64 = id.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b64));
            return path.Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return false; }
    }

    private bool IsUnderRoot(string full)
    {
        foreach (var root in _roots)
            if (full.StartsWith(root, _cmp)) return true;
        return false;
    }

    /// <summary>Fully qualified, resolved, with exactly one trailing separator, so <c>C:\a\root</c> never admits <c>C:\a\root-evil</c>.</summary>
    private static string? NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var r = Environment.ExpandEnvironmentVariables(root.Trim());
        if (!Path.IsPathFullyQualified(r)) return null;
        try
        {
            var full = Path.GetFullPath(r);
            return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string StripQuery(string s)
    {
        var q = s.IndexOfAny(new[] { '?', '#' });
        return q >= 0 && s.Contains("://", StringComparison.Ordinal) ? s[..q] : s;
    }
}
