using AliceVoice.Core.Config;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Decides which SOUL file Alice LOCAL reads and which file the SOUL editor
/// may write. The editor only ever writes the Alice LOCAL SOUL
/// (<see cref="AliceVoiceOptions.ResolvedAliceLocalSoulPath"/>); the Hermes
/// alice_voice profile SOUL is a read-only fallback and is never written.
/// </summary>
public static class SoulResolver
{
    /// <summary>The SOUL Alice LOCAL should load: its own if present, else the read-only fallback.</summary>
    public static string? ReadPath(AliceVoiceOptions o, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (fileExists(o.ResolvedAliceLocalSoulPath)) return o.ResolvedAliceLocalSoulPath;
        if (fileExists(o.ResolvedSoulFallbackPath)) return o.ResolvedSoulFallbackPath;
        return null;
    }

    /// <summary>
    /// True if the editor may write <paramref name="path"/>: it must be the
    /// configured Alice LOCAL SOUL, must not be the fallback, and must not live
    /// under any Hermes profile folder.
    /// </summary>
    public static bool IsWritable(AliceVoiceOptions o, string path)
    {
        var full = Normalize(path);
        if (!string.Equals(full, Normalize(o.ResolvedAliceLocalSoulPath), StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(full, Normalize(o.ResolvedSoulFallbackPath), StringComparison.OrdinalIgnoreCase)) return false;
        var profiles = Normalize(Path.Combine(o.HermesHome, "profiles")) + Path.DirectorySeparatorChar;
        return !full.StartsWith(profiles, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
