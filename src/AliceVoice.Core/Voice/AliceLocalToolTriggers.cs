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
