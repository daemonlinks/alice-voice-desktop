namespace AliceVoice.Universal.Voice;

/// <summary>
/// What a copy gesture puts on the clipboard. A highlighted range wins.
/// With no highlight, the whole message is copied.
/// </summary>
public static class TranscriptCopy
{
    public const string Action = "Copy";

    public static string Text(string? message, string? selected)
    {
        if (!string.IsNullOrEmpty(selected)) return selected;
        return message ?? "";
    }
}
