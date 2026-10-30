using System.Text;

namespace AliceVoice.Core.Audio;

/// <summary>
/// Splits text for TTS engines with an input limit (AllTalk rejects &gt;1000
/// chars). Splits at sentence ends, then at commas/spaces, and only cuts a
/// word as a last resort — the old fixed-width split cut words in half.
/// </summary>
public static class TextChunker
{
    public static IEnumerable<string> Chunk(string text, int max = 900)
    {
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max));
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            if (rest.Length <= max) { yield return rest; yield break; }
            var cut = LastBreak(rest, max, ".!?\n") ?? LastBreak(rest, max, ",;:") ?? LastBreak(rest, max, " ") ?? max;
            var piece = rest[..cut].Trim();
            if (piece.Length > 0) yield return piece;
            rest = rest[cut..].TrimStart();
        }
    }

    /// <summary>Index just after the last break char within the first <paramref name="max"/> chars (not too early).</summary>
    private static int? LastBreak(string s, int max, string chars)
    {
        for (int i = Math.Min(max, s.Length) - 1; i >= max / 4; i--)
            if (chars.IndexOf(s[i]) >= 0) return i + 1;
        return null;
    }
}
