using System.Text.Json.Nodes;

namespace AliceVoice.Core.Voice;

/// <summary>
/// The Keywords &amp; Tools phrases from the Alice Voice web panel.
/// A match becomes the same <c>{"tool","args"}</c> call the model is taught to emit.
/// Phrases that need conversation context (a bare "I don't like that anymore")
/// are left for the model, which has the tool instructions.
/// </summary>
public static class AliceLocalToolTriggers
{
    public static bool TryMatch(string? utterance, out string tool, out string argsJson)
    {
        tool = "";
        argsJson = "{}";
        if (string.IsNullOrWhiteSpace(utterance)) return false;

        if (TryPrefix(utterance, "remember that", out var fact))
            return fact.Length > 0 && Save(fact, out tool, out argsJson);
        if (TryPrefix(utterance, "remember i", out var afterI))
        {
            var remembered = afterI.Length == 0 ? "" : "I " + afterI;
            return remembered.Length > 0 && Save(remembered, out tool, out argsJson);
        }
        if (IsExact(utterance, "what do you know about me"))
            return Call("honcho_search", Args(("query", "Master")), out tool, out argsJson);
        if (IsExact(utterance, "what time is it"))
            return Call("time", "{}", out tool, out argsJson);
        if (TryPrefix(utterance, "search for", out var search))
            return search.Length > 0 && Call("search", Args(("query", search)), out tool, out argsJson);
        if (TryPrefix(utterance, "forget that", out var forget)
            || TryPrefix(utterance, "remove that", out forget)
            || TryPrefix(utterance, "delete that", out forget))
            return forget.Length > 0 && Call("honcho_forget", Args(("query", forget), ("content", forget)), out tool, out argsJson);
        return false;
    }

    private static bool Save(string content, out string tool, out string argsJson)
        => Call("honcho_save", Args(("content", content)), out tool, out argsJson);

    /// <summary>
    /// A greeting or a scrap of small talk: nothing to look up, nothing to show. Such a turn is answered
    /// with the conversation model alone - no tools, no tool instructions, no tool loop. (llama3.1 answered
    /// "Hi" with four honcho calls and then an apology, 2026-11-24.)
    /// </summary>
    public static bool IsSmallTalk(string? utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return false;
        var folded = Fold(utterance);
        if (folded.Length == 0) return false;
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 6) return false;
        if (SmallTalk.Contains(folded)) return true;
        foreach (var word in words)
            if (Asks.Contains(word)) return false;
        return words.Length <= 4 && Greetings.Contains(words[0]);
    }

    /// <summary>Lower case, punctuation stripped, whitespace folded - so "How's it going?" meets "hows it going".</summary>
    private static string Fold(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is '\'' or '\u2019') continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static readonly HashSet<string> SmallTalk = new(StringComparer.Ordinal)
    {
        "how are you", "how are you doing", "hows it going", "how is it going", "how do you do",
        "you alright", "are you alright", "you ok", "you okay", "good morning", "good afternoon",
        "good evening", "good night", "night night", "i love you", "love you", "thank you",
        "thanks babe", "hi babe", "hello babe", "hi love", "hello love", "hi master", "hello master",
        "you there", "are you there", "you awake", "are you awake"
    };

    private static readonly HashSet<string> Greetings = new(StringComparer.Ordinal)
    {
        "hi", "hey", "hello", "hullo", "hiya", "yo", "good", "morning", "afternoon", "evening", "night",
        "nighty", "thanks", "thank", "cheers", "love"
    };

    private static readonly HashSet<string> Asks = new(StringComparer.Ordinal)
    {
        "show", "picture", "pictures", "photo", "photos", "image", "images", "film", "films", "video",
        "videos", "search", "look", "find", "remember", "forget", "save", "delete", "remove", "remind",
        "time", "what", "when", "where", "who", "why", "how", "which", "list", "play", "open", "draw",
        "make", "write", "tell", "send", "can", "could", "would", "will", "please", "meter", "orgasm"
    };

    private static bool Call(string name, string args, out string tool, out string argsJson)
    {
        tool = name;
        argsJson = args;
        return true;
    }

    private static string Args(params (string Key, string Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields) obj[key] = value;
        return obj.ToJsonString();
    }

    private static string Core(string original)
    {
        var work = original.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'');
        var lower = work.ToLowerInvariant();
        if (lower.StartsWith("please ")) work = work[7..].TrimStart();
        else if (lower.StartsWith("alice, ")) work = work[7..].TrimStart();
        else if (lower.StartsWith("alice ")) work = work[6..].TrimStart();
        return work.Trim();
    }

    private static bool IsExact(string original, string phrase)
    {
        var folded = Core(original).ToLowerInvariant().Trim().TrimEnd('.', '?', '!', '\u2026').Trim();
        return folded == phrase;
    }

    private static bool TryPrefix(string original, string phrase, out string remainder)
    {
        remainder = "";
        var core = Core(original);
        var folded = core.ToLowerInvariant();
        if (!folded.StartsWith(phrase, StringComparison.Ordinal)) return false;
        if (folded.Length > phrase.Length)
        {
            var boundary = folded[phrase.Length];
            if (boundary is not (' ' or ',' or ':' or '.' or '?' or '!')) return false;
        }
        remainder = core[phrase.Length..].Trim().TrimStart(',', ':', '-', ' ').Trim().TrimEnd('.', '?', '!', '\u2026').Trim();
        return true;
    }
}
