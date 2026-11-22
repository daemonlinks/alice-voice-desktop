namespace AliceVoice.Universal.Voice;

using AliceVoice.Core.Voice;

/// <summary>
/// Who speaks with what when nobody has chosen a voice.
///
/// Master (2026-11-22): "if a voice has not been set then the next unique (unused) female voice based
/// on quality is allocated". Quality is read off the voice's OWN name rather than guessed - piper says
/// x_low | low | medium | high in the model name, and Edge's "Neural" voices are its natural ones - so
/// the ranking is measured. The allocation is deterministic: best quality first, then A-Z, and it skips
/// every voice already in use so two girls never share one.
/// </summary>
public static class VoicePolicy
{
    private static readonly string[] FemaleNames =
    {
        "female", "woman", "girl", "alba", "sonia", "libby", "maisie", "jenny", "aria", "emily", "amy",
        "olivia", "sophie", "hollie", "charlotte", "ellie", "nova", "bella", "abbi", "lily", "kate",
        "serena", "jane", "michelle", "natasha", "clara", "isabella", "samantha", "victoria", "zira",
        "cori", "kathleen", "patti", "linda", "heather",
    };

    private static readonly string[] MaleNames =
    {
        "george", "ryan", "guy", "david", "mark", "james", "arthur", "daniel", "fred", "oliver",
        "thomas", "liam", "harry", "alan", "joe", "male", "northern_english_male", "southern_english_male",
    };

    /// <summary>A voice name as the stored assignment wants it: no file suffix, no padding.</summary>
    public static string Normalise(string? voice) =>
        (voice ?? "").Trim().Replace(".wav", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Is this a female voice? "female" is tested first so it is never read as the "...male" inside it;
    /// a name carrying no gender at all counts as usable rather than being thrown away.
    /// </summary>
    public static bool LooksFemale(string? voice)
    {
        var v = Normalise(voice).ToLowerInvariant();
        if (v.Length == 0) return false;
        if (FemaleNames.Any(n => v.Contains(n, StringComparison.Ordinal))) return true;
        return !MaleNames.Any(n => v.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>Bigger is better. Piper names its tiers; Edge's Neural voices are its natural ones.</summary>
    public static int Quality(string? voice)
    {
        var v = Normalise(voice).ToLowerInvariant();
        if (v.Contains("x_high", StringComparison.Ordinal) || v.Contains("xhigh", StringComparison.Ordinal)) return 100;
        if (v.Contains("high", StringComparison.Ordinal)) return 90;
        if (v.Contains("neural", StringComparison.Ordinal) || v.Contains("natural", StringComparison.Ordinal)) return 80;
        if (v.Contains("medium", StringComparison.Ordinal)) return 60;
        if (v.Contains("low", StringComparison.Ordinal)) return 20;   // covers x_low as well
        return 50;                                                     // no tier in the name: AllTalk's female_XX
    }

    /// <summary>
    /// The next unused female voice, best quality first. Returns null when every female voice the
    /// provider offers is already taken - the caller then leaves the existing settings alone rather
    /// than quietly stealing another girl's voice.
    /// </summary>
    public static string? NextUnusedFemale(IEnumerable<string?> used, IEnumerable<string> candidates)
    {
        var taken = new HashSet<string>(
            used.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => Normalise(u)),
            StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(c => LooksFemale(c) && !taken.Contains(Normalise(c)))
            .OrderByDescending(Quality)
            .ThenBy(c => Normalise(c), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// A distinct voice for one backend, across engines: its own engine is searched first (never move a
    /// backend off the engine it already uses if that engine has anything left), then the preferred one,
    /// then the rest. This is what stops two backends sounding alike when one provider runs out - Master,
    /// 2026-11-22: "it would be nice if the voices were different for all backends to avoid confusing me".
    /// </summary>
    public static (VoiceProviderKind Provider, string Voice)? NextDistinctFemale(
        IEnumerable<string?> used,
        IReadOnlyList<(VoiceProviderKind Provider, IReadOnlyList<string> Voices)> catalogues,
        VoiceProviderKind? prefer = null)
    {
        var order = new List<VoiceProviderKind>();
        if (prefer is { } first) order.Add(first);
        order.AddRange(catalogues.Select(c => c.Provider).Where(p => !order.Contains(p)));
        foreach (var provider in order)
        {
            var entry = catalogues.FirstOrDefault(c => c.Provider == provider);
            if (entry.Voices is null || entry.Voices.Count == 0) continue;
            var voice = NextUnusedFemale(used, entry.Voices);
            if (voice is not null) return (provider, voice);
        }
        return null;
    }
}
