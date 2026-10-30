namespace AliceVoice.Core.Voice;

/// <summary>
/// Turns the UI's mood chip + response profile into per-turn
/// <see cref="AskOptions"/> (token cap + style line in the system prompt).
/// Applied to the local Ollama backends; fleet agents own their own style.
/// </summary>
public static class ReplyStyle
{
    public static readonly string[] Moods = { "natural", "comfort", "playful", "flirty", "focused" };
    public static readonly string[] Profiles = { "quick", "natural", "full" };

    public static int MaxTokens(string profile) => profile switch
    {
        "quick" => 160,
        "full" => 1024,
        _ => 320
    };

    public static string? MoodLine(string mood) => mood switch
    {
        "comfort" => "Mood for this reply: gentle, warm and reassuring.",
        "playful" => "Mood for this reply: light-hearted, teasing and playful.",
        "flirty" => "Mood for this reply: affectionate and flirtatious.",
        "focused" => "Mood for this reply: concise, precise and on-task.",
        _ => null
    };

    public static string? LengthLine(string profile) => profile switch
    {
        "quick" => "Keep this reply to 1–2 short sentences.",
        "full" => null,
        _ => "Keep this reply to 2–3 conversational sentences."
    };

    public static AskOptions Build(string mood, string profile)
    {
        var lines = new[] { MoodLine(mood), LengthLine(profile) }.Where(l => l is not null);
        var hint = string.Join(" ", lines);
        return new AskOptions
        {
            MaxTokens = MaxTokens(profile),
            StyleHint = hint.Length == 0 ? null : hint
        };
    }
}
