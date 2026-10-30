namespace AliceVoice.Core.Stt;

public enum PttAction { None, StartRecording, StopAndTranscribe }

/// <summary>
/// Hold-to-talk with a tap-to-latch fallback:
/// hold the button → recording while held, transcribed once on release;
/// a quick tap (&lt; <see cref="LatchThreshold"/>) latches recording on and
/// the next press stops it; keyboard activation toggles.
/// </summary>
public sealed class PushToTalkGesture
{
    public enum State { Idle, Holding, Latched }

    public static readonly TimeSpan LatchThreshold = TimeSpan.FromMilliseconds(350);

    private DateTime _pressedAt;
    private bool _ignoreNextRelease;

    public State Current { get; private set; } = State.Idle;

    public PttAction Press(DateTime now)
    {
        switch (Current)
        {
            case State.Idle:
                Current = State.Holding;
                _pressedAt = now;
                return PttAction.StartRecording;
            case State.Latched:
                Current = State.Idle;
                _ignoreNextRelease = true;
                return PttAction.StopAndTranscribe;
            default:
                return PttAction.None;
        }
    }

    public PttAction Release(DateTime now)
    {
        if (_ignoreNextRelease) { _ignoreNextRelease = false; return PttAction.None; }
        if (Current != State.Holding) return PttAction.None;
        if (now - _pressedAt < LatchThreshold)
        {
            Current = State.Latched;
            return PttAction.None;
        }
        Current = State.Idle;
        return PttAction.StopAndTranscribe;
    }

    /// <summary>Pointer capture lost / cancelled while holding: finish the utterance.</summary>
    public PttAction Cancel()
    {
        _ignoreNextRelease = false;
        if (Current != State.Holding) return PttAction.None;
        Current = State.Idle;
        return PttAction.StopAndTranscribe;
    }

    /// <summary>Keyboard (Space/Enter) activation toggles recording.</summary>
    public PttAction Toggle()
    {
        if (Current == State.Idle) { Current = State.Latched; return PttAction.StartRecording; }
        Current = State.Idle;
        return PttAction.StopAndTranscribe;
    }

    /// <summary>Abort without transcribing (mode change, window closing).</summary>
    public void Reset()
    {
        Current = State.Idle;
        _ignoreNextRelease = false;
    }
}
