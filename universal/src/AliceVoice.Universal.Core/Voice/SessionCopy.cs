namespace AliceVoice.Universal.Voice;

/// <summary>User-facing strings copied from the WinUI window so both heads say the same thing.</summary>
public static class SessionCopy
{
    public const string Startup = "Talk to me, Master. Hold 🎤 Talk while you speak.";
    public const string VoiceTestLine = "Hello Master. This is a voice quality test.";
    public const string ContextSliderReason = "Not implemented yet — context injection is controlled by the agent.";
    public const string SoulFleetNotice = "The SOUL editor edits Alice LOCAL's own SOUL — select Alice LOCAL as the Agent first.";
    public const string SoulFleetStatus = "SOUL editor is only available on Alice LOCAL";
    public const string HelpTriggers = "Remember that… · Remember I… · What do you know about me? · Forget that… · Remove that… · Delete that… · I don't like that anymore · Search for…";
    public const string HelpTools = "honcho_search · honcho_save · honcho_forget · search (Alice LOCAL)";
    public const string StyleFleetTip = "Applies to Alice LOCAL only — fleet agents use their own Hermes profile settings.";
    public const string RememberFleetTip = "Remember saves to Alice LOCAL's memory — available when talking to Alice LOCAL.";
    public const string TalkTip = "Hold to talk — release to send. Tap to latch on, tap again to send.";

    public static string ProfileHint(string profile) => profile switch
    {
        "quick" => $"1–2 short sentences · tightest loop · max {AliceVoice.Core.Voice.ReplyStyle.MaxTokens("quick")} tokens",
        "full" => $"longer, richer replies · slower loop · max {AliceVoice.Core.Voice.ReplyStyle.MaxTokens("full")} tokens",
        _ => $"2–3 sentences · conversational default · max {AliceVoice.Core.Voice.ReplyStyle.MaxTokens("natural")} tokens"
    };

    public static string SoulLoaded(AliceVoice.Core.Voice.SoulDocument doc) => doc.CreatedFromTemplate
        ? $"No Alice LOCAL SOUL existed, so a neutral starter template was created at {doc.Path}. Edit it and Save. Agent SOULs are never used."
        : $"Editing Alice LOCAL SOUL ({doc.Path})";

    public static string SoulSaved(AliceVoice.Core.Voice.SoulSaveResult result) => result.BackupPath is null
        ? $"Saved {result.Path} — applies on the next reply"
        : $"Saved (backup: {Path.GetFileName(result.BackupPath)}) — applies on the next reply";
}
