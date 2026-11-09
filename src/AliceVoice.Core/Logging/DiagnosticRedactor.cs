using System.Text;
using System.Text.RegularExpressions;

namespace AliceVoice.Core.Logging;

/// <summary>
/// Strips credential-like values, tool arguments, tool results, and prompt-shaped
/// JSON fields from diagnostic text. Ordinary exception messages are left intact.
/// </summary>
public static class DiagnosticRedactor
{
    private static readonly HashSet<string> JsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "args", "content", "query", "prompt", "api_key", "apikey", "api-key",
        "token", "secret", "password", "authorization", "access_token", "refresh_token",
        "honcho_api_key", "tavily_api_key", "x-api-key"
    };

    private static readonly Regex BearerPattern = new(
        """(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JwtPattern = new(
        """\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}\b""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecretKeyPattern = new(
        """\bsk-[A-Za-z0-9]{12,}\b""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AssignmentPattern = new(
        """(?i)\b((?:api[_-]?key|access[_-]?token|refresh[_-]?token|x-api-key|authorization|token|secret|password|passwd|credential|honcho_api_key|tavily_api_key))\b(\s*[:=]\s*)(?:"[^"]*"|'[^']*'|\S+)""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Returns <paramref name="text"/> with sensitive values replaced. Null becomes an empty string.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var redacted = BearerPattern.Replace(text, "Bearer [redacted]");
        redacted = JwtPattern.Replace(redacted, "[redacted-jwt]");
        redacted = SecretKeyPattern.Replace(redacted, "[redacted-key]");
        redacted = AssignmentPattern.Replace(redacted, "$1$2[redacted]");
        redacted = RedactJsonValues(redacted);
        redacted = RedactToolResults(redacted);
        return redacted;
    }

    /// <summary>One line, length-capped, redacted. Used for breadcrumb details.</summary>
    public static string OneLine(string? text, int maxLength)
    {
        var redacted = Redact(text).Replace("\r", " ").Replace("\n", " ").Trim();
        if (redacted.Length <= maxLength) return redacted;
        return redacted[..maxLength];
    }

    private static string RedactToolResults(string text)
    {
        const string marker = " result:";
        var index = 0;
        while (true)
        {
            var at = text.IndexOf("[TOOL ", index, StringComparison.Ordinal);
            if (at < 0) return text;
            var result = text.IndexOf(marker, at, StringComparison.Ordinal);
            if (result < 0 || result - at > 80) return text;
            var start = result + marker.Length;
            var end = text.IndexOf(']', start);
            if (end < 0 || end - start > 8000) end = Math.Min(text.Length, start + 200);
            text = string.Concat(text.AsSpan(0, start), " [redacted]", text.AsSpan(end));
            index = start + " [redacted]".Length;
        }
    }

    private static string RedactJsonValues(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                builder.Append(text[i]);
                continue;
            }

            var keyEnd = IndexOfUnescapedQuote(text, i + 1);
            if (keyEnd < 0)
            {
                builder.Append(text[i]);
                continue;
            }

            var key = text[(i + 1)..keyEnd];
            var cursor = keyEnd + 1;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length || text[cursor] != ':' || !JsonKeys.Contains(key))
            {
                builder.Append(text[i]);
                continue;
            }

            cursor++;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= text.Length)
            {
                builder.Append(text.AsSpan(i));
                break;
            }

            var valueEnd = EndOfJsonValue(text, cursor);
            builder.Append('"').Append(key).Append("\":[redacted]");
            i = valueEnd - 1;
        }
        return builder.ToString();
    }

    private static int IndexOfUnescapedQuote(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        return -1;
    }

    private static int EndOfJsonValue(string text, int start)
    {
        var next = start + 1;
        if (next > text.Length) return text.Length;
        switch (text[start])
        {
            case '"':
                var end = IndexOfUnescapedQuote(text, start + 1);
                return end < 0 ? text.Length : end + 1;
            case '{':
                return EndOfBracket(text, start, '{', '}');
            case '[':
                return EndOfBracket(text, start, '[', ']');
            default:
                var i = start;
                while (i < text.Length && text[i] is not (',' or '}' or ']' or '\r' or '\n')) i++;
                return i;
        }
    }

    private static int EndOfBracket(string text, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return text.Length;
    }
}
