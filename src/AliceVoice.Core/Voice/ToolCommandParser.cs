using System.Text;
using System.Text.Json;

namespace AliceVoice.Core.Voice;

/// <summary>One tool command found in a reply: <c>{"tool": "&lt;name&gt;", "args": {...}}</c>.</summary>
public sealed record ToolCommand(string Tool, string ArgsJson);

/// <summary>A reply split into the tool commands it carried and the words around them.</summary>
public sealed record ParsedReply(IReadOnlyList<ToolCommand> Commands, string Prose)
{
    public bool HasCommands => Commands.Count > 0;
}

/// <summary>
/// The tool-command convention shared by the Alice Voice relay (f1124a7) and the
/// remote-backend contract (skill <c>alice-voice-remote-backend</c>): a reply may
/// carry a JSON object with a string <c>tool</c> field ANYWHERE - alone, after a
/// few words, or inside a code fence. The object is a control message. It is run,
/// and it is never shown or spoken. Parsing is a string-aware brace scan, so braces
/// inside JSON strings do not end the object early.
/// </summary>
public static class ToolCommandParser
{
    /// <summary>Every complete tool command in <paramref name="reply"/>, in order, plus the prose with them removed.</summary>
    public static ParsedReply Parse(string? reply)
    {
        if (string.IsNullOrEmpty(reply)) return new ParsedReply(Array.Empty<ToolCommand>(), "");
        var commands = new List<ToolCommand>();
        var cuts = new List<(int Start, int End)>();
        for (var i = 0; i < reply.Length; i++)
        {
            if (reply[i] != '{') continue;
            if (!TrySliceJson(reply, i, out var slice))
            {
                // An unclosed object that is already naming a tool is a call that was cut
                // off (streaming, max_tokens). It is still not for the ear.
                if (LooksLikeToolStart(reply, i)) { cuts.Add((i, reply.Length)); break; }
                continue;
            }
            if (TryReadCommand(slice, out var command))
            {
                commands.Add(command);
                cuts.Add((i, i + slice.Length));
                i += slice.Length - 1;
            }
        }
        if (cuts.Count == 0) return new ParsedReply(commands, reply.Trim());
        var sb = new StringBuilder(reply.Length);
        var at = 0;
        foreach (var (start, end) in cuts)
        {
            sb.Append(reply, at, start - at);
            sb.Append(' ');
            at = end;
        }
        if (at < reply.Length) sb.Append(reply, at, reply.Length - at);
        return new ParsedReply(commands, Tidy(sb.ToString()));
    }

    /// <summary>The first tool command, the same answer Alice LOCAL's parser has always given.</summary>
    public static bool TryParseFirst(string? reply, out string tool, out string argsJson)
    {
        var parsed = Parse(reply);
        if (parsed.Commands.Count == 0)
        {
            tool = "";
            argsJson = "{}";
            return false;
        }
        tool = parsed.Commands[0].Tool;
        argsJson = parsed.Commands[0].ArgsJson;
        return true;
    }

    /// <summary>The words only: every tool command (complete or cut off) and any code fence left empty by it are removed.</summary>
    public static string StripForSpeech(string? reply) => Parse(reply).Prose;

    /// <summary>
    /// True while streamed text may be in the middle of a tool command, so a
    /// partial bubble or voice must not show it yet.
    /// </summary>
    public static bool HoldsToolCall(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith("```")) return true;
        var brace = text.IndexOf('{');
        if (brace < 0) return false;
        if (text.IndexOf("\"tool\"", brace, StringComparison.Ordinal) >= 0) return true;
        // The OpenAI spelling of the same thing, mid-sentence.
        return text.IndexOf("\"name\"", brace, StringComparison.Ordinal) >= 0
            && (text.IndexOf("\"parameters\"", brace, StringComparison.Ordinal) >= 0
                || text.IndexOf("\"arguments\"", brace, StringComparison.Ordinal) >= 0);
    }

    private static bool TryReadCommand(string slice, out ToolCommand command)
    {
        command = new ToolCommand("", "{}");
        try
        {
            using var doc = JsonDocument.Parse(slice);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // The documented shape: {"tool": "<name>", "args": {...}}
            if (root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var name = t.GetString() ?? "";
                if (name.Length == 0) return false;
                var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}";
                command = new ToolCommand(name, args);
                return true;
            }
            // What a model that has only ever seen OpenAI-style calls writes instead:
            // {"name": "gui_list_pictures", "parameters": {}} - and the "arguments" spellings,
            // bare and inside {"function": {...}}. Found leaking into a spoken bubble on
            // 2026-11-24: the shape the model knows from training must never be the shape that
            // goes unparsed, or the ear hears raw JSON.
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && IsToolishName(n.GetString() ?? ""))
            {
                command = new ToolCommand(n.GetString()!, ReadArgs(root));
                return true;
            }
            if (root.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object
                && fn.TryGetProperty("name", out var fnName) && fnName.ValueKind == JsonValueKind.String
                && IsToolishName(fnName.GetString() ?? ""))
            {
                command = new ToolCommand(fnName.GetString()!, ReadArgs(fn));
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The arguments object under whichever name the provider uses, or the string it hides in.</summary>
    private static string ReadArgs(JsonElement owner)
    {
        foreach (var key in new[] { "parameters", "arguments", "args", "input" })
        {
            if (!owner.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Object) return v.GetRawText();
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = (v.GetString() ?? "").Trim();
                if (raw.StartsWith('{')) return raw;
            }
        }
        return "{}";
    }

    /// <summary>
    /// A name a tool could have: lower snake_case. Keeps an ordinary JSON object that merely
    /// carries a "name" - {"name": "Master", "age": 52} - out of the parser's teeth.
    /// </summary>
    private static bool IsToolishName(string name)
    {
        if (name.Length is < 2 or > 40) return false;
        if (!char.IsAsciiLetterLower(name[0])) return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')) return false;
        return name.Contains('_') || name is "time" or "search";
    }

    private static bool LooksLikeToolStart(string text, int brace)
    {
        var j = brace + 1;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        return string.CompareOrdinal(text, j, "\"tool\"", 0, 6) == 0
            || string.CompareOrdinal(text, j, "\"name\"", 0, 6) == 0
            || string.CompareOrdinal(text, j, "\"function\"", 0, 10) == 0;
    }

    /// <summary>Remove code fences that now hold nothing, then fold the whitespace a cut leaves behind.</summary>
    private static string Tidy(string text)
    {
        var s = text;
        while (true)
        {
            var open = s.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) break;
            var close = s.IndexOf("```", open + 3, StringComparison.Ordinal);
            var inner = close < 0 ? s[(open + 3)..] : s[(open + 3)..close];
            var body = inner.Trim();
            // ```json / ```JSON / ``` - a language tag alone is still empty.
            if (body.Length == 0 || body.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                s = close < 0 ? s[..open] : s[..open] + " " + s[(close + 3)..];
                continue;
            }
            break;
        }
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (c is ' ' or '\t')
            {
                if (!space && sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
                space = true;
                continue;
            }
            if (c == '\r') continue;
            if (c == '\n')
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                if (!(sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n')) sb.Append('\n');
                space = false;
                continue;
            }
            sb.Append(c);
            space = false;
        }
        var result = sb.ToString().Trim();
        // " ." left where a call sat between a word and its full stop.
        return result.Replace(" .", ".", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal);
    }

    internal static bool TrySliceJson(string text, int start, out string json)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
        }
        json = "";
        return false;
    }
}
