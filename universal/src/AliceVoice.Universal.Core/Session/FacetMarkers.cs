using System.Text.RegularExpressions;

namespace AliceVoice.Universal.Session;

/// <summary>Alice's facets: one soul, two girls. Plum is defined in the SOUL under `## Plum`.</summary>
public enum Facet { Alice, Plum }

/// <summary>
/// The speaker-marker convention, shared with the voice page so both legs split a reply identically.
///
/// Master (2026-11-22): "[Plum] at the start of her character block", as a temporary override of the
/// alice_voice voice settings - her bubble in HER colour, spoken in HER voice, and the next block hands
/// his settings straight back. relay.py's <c>_SPKR_RE</c> is the source of truth; this is the same
/// pattern. Never let the two drift apart - a marker the page honours and the app ignores shows two
/// girls on one screen and one voice in the room.
/// </summary>
public static class FacetMarkers
{
    private static readonly Regex Marker = new(
        @"(?:\*{1,2}\s*\[\s*(?<f>Plum|Alice)\s*\]\s*\*{1,2}"
        + @"|\*{1,2}\s*(?<f>Plum|Alice)\s*\*{1,2}"
        + @"|\[\s*(?<f>Plum|Alice)\s*\]"
        + @"|^\s*(?<f>Plum|Alice)(?:[.,!?;:]|$)\s*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>True when the reply carries any speaker marker at all.</summary>
    public static bool HasMarker(string? text) => !string.IsNullOrWhiteSpace(text) && Marker.IsMatch(text);

    /// <summary>
    /// The reply with every marker taken out - what a single-bubble turn shows and says. The whitespace
    /// the marker was wrapped in goes with it, so removing a marker never leaves a stray blank line; only
    /// the markers' own space is touched, so genuine paragraphs in the reply stay where the writer put them.
    /// </summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        const string hole = "\u0001";
        return Regex.Replace(Marker.Replace(text, hole), @"\s*" + hole + @"\s*", " ").Trim();
    }

    /// <summary>
    /// Split a reply into its speaker blocks. Text before the first marker is Alice's, so an untagged
    /// reply comes back as one Alice block and nothing about the app's old behaviour changes.
    /// </summary>
    public static List<(Facet Facet, string Text)> Split(string? reply)
    {
        var blocks = new List<(Facet, string)>();
        if (string.IsNullOrWhiteSpace(reply)) return blocks;
        var current = Facet.Alice;
        var start = 0;
        foreach (Match m in Marker.Matches(reply))
        {
            var text = reply[start..m.Index].Trim();
            if (text.Length > 0) blocks.Add((current, text));
            current = m.Groups["f"].Value.Equals("plum", StringComparison.OrdinalIgnoreCase) ? Facet.Plum : Facet.Alice;
            start = m.Index + m.Length;
        }
        var tail = reply[start..].Trim();
        if (tail.Length > 0) blocks.Add((current, tail));
        return blocks;
    }
}
