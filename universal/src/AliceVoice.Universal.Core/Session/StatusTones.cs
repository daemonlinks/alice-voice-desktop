namespace AliceVoice.Universal.Session;

/// <summary>
/// Live status colours. Green is connected or a stage that succeeded.
/// Amber is work in progress (connecting, listening, transcribing, thinking, speaking).
/// Red is offline or a failed stage. Off is an idle stage that has not run.
/// Hex values match Resources/Styles/Colors.xaml (AppGreen, AppYellow, AppRed).
/// </summary>
public enum StatusTone { Off, Green, Amber, Red }

public static class StatusTones
{
    public const string GreenHex = "#22C55E";
    public const string AmberHex = "#EAB308";
    public const string RedHex = "#EF4444";
    public const string OffHex = "#3F3F4A";

    public static string Hex(StatusTone tone) => tone switch
    {
        StatusTone.Green => GreenHex,
        StatusTone.Amber => AmberHex,
        StatusTone.Red => RedHex,
        _ => OffHex
    };

    public static StatusTone ForStage(StageLamp lamp) => lamp switch
    {
        StageLamp.Green => StatusTone.Green,
        StageLamp.Yellow or StageLamp.Wait => StatusTone.Amber,
        StageLamp.Red => StatusTone.Red,
        _ => StatusTone.Off
    };

    public static StatusTone ForNote(NoteKind kind) => kind switch
    {
        NoteKind.Green => StatusTone.Green,
        NoteKind.Yellow => StatusTone.Amber,
        NoteKind.Red => StatusTone.Red,
        _ => StatusTone.Off
    };

    /// <summary>The link to Alice. In-progress turns stay green here; the activity dot carries those.</summary>
    public static StatusTone Presence(bool connected, string activity)
    {
        if (activity == "Connecting…") return StatusTone.Amber;
        if (!connected || activity == "Offline") return StatusTone.Red;
        return StatusTone.Green;
    }

    public static StatusTone Activity(string activity, bool connected)
    {
        if (activity == "Connecting…") return StatusTone.Amber;
        if (!connected || activity == "Offline") return StatusTone.Red;
        if (activity is "Listening…" or "Transcribing…" or "Thinking…" or "Speaking…")
            return StatusTone.Amber;
        if (activity == "Ready") return StatusTone.Green;
        return StatusTone.Off;
    }

    /// <summary>Capturing speech is amber. Mic off is red. An open idle mic (wake armed) is green.</summary>
    public static StatusTone Mic(StageLamp micLamp, StageLamp micTag, string activity)
    {
        if (activity == "Listening…") return StatusTone.Amber;
        if (micTag == StageLamp.Red) return StatusTone.Red;
        return ForStage(micLamp);
    }

    /// <summary>
    /// The stage that owns the current activity is amber even if its lamp was left green
    /// (TTS is marked green while it is still speaking).
    /// </summary>
    public static StatusTone Pipeline(StageLamp lamp, string activity, string activeActivity)
    {
        if (activity == activeActivity) return StatusTone.Amber;
        return ForStage(lamp);
    }

    public static string Caption(string name, StatusTone tone) => tone switch
    {
        StatusTone.Green => name + " ok",
        StatusTone.Amber => name + " busy",
        StatusTone.Red => name + " down",
        _ => name + " idle"
    };
}
