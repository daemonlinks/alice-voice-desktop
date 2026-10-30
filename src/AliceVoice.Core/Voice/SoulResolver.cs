using AliceVoice.Core.Config;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Decides which SOUL file Alice LOCAL reads and which file the SOUL editor
/// may write. Alice LOCAL uses ONLY its own local SOUL
/// (<see cref="AliceVoiceOptions.ResolvedAliceLocalSoulPath"/>, shared with the
/// Alice Voice web app). No agent SOUL (any Hermes profile SOUL) is ever read
/// as a fallback or written by the editor.
/// </summary>
public static class SoulResolver
{
    /// <summary>
    /// The SOUL Alice LOCAL should load: its own local file if it exists and is
    /// not an agent SOUL, otherwise null (never any other file).
    /// </summary>
    public static string? ReadPath(AliceVoiceOptions o, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var local = o.ResolvedAliceLocalSoulPath;
        if (IsAgentSoulPath(o, local)) return null;
        return fileExists(local) ? local : null;
    }

    /// <summary>
    /// True if the editor may write <paramref name="path"/>: it must be the
    /// configured Alice LOCAL SOUL and must not live under any Hermes profile
    /// folder (agent SOULs).
    /// </summary>
    public static bool IsWritable(AliceVoiceOptions o, string path)
    {
        var full = Normalize(path);
        if (!string.Equals(full, Normalize(o.ResolvedAliceLocalSoulPath), StringComparison.OrdinalIgnoreCase)) return false;
        return !IsAgentSoulPath(o, full);
    }

    /// <summary>True if <paramref name="path"/> is inside the Hermes profiles folder (an agent's SOUL/config).</summary>
    public static bool IsAgentSoulPath(AliceVoiceOptions o, string path)
    {
        var profiles = Normalize(Path.Combine(o.HermesHome, "profiles")) + Path.DirectorySeparatorChar;
        return Normalize(path).StartsWith(profiles, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}