namespace AliceVoice.Core.WakeWord;

public enum EndOfSpeechState { WaitingForSpeech, Speaking, Ended, NoSpeech, MaxLength }

/// <summary>Energy-VAD settings for the command spoken after the wake word.</summary>
public sealed record EndOfSpeechSettings
{
    /// <summary>Frame RMS (0..1 full scale) counted as speech. 0.015 is about -36 dBFS.</summary>
    public float SpeechRms { get; init; } = 0.015f;

    /// <summary>Silence needed after speech to end the utterance.</summary>
    public int EndSilenceMs { get; init; } = 900;

    /// <summary>Speech needed before silence can end the utterance (ignores clicks).</summary>
    public int MinSpeechMs { get; init; } = 250;

    /// <summary>Give up if no speech starts within this time after the wake word.</summary>
    public int NoSpeechTimeoutMs { get; init; } = 5000;

    /// <summary>Hard cap on one utterance.</summary>
    public int MaxUtteranceMs { get; init; } = 30000;
}

/// <summary>
/// Decides when the command after the wake word is finished, from frame
/// energy: waits for speech, then ends after <see cref="EndOfSpeechSettings.EndSilenceMs"/>
/// of silence; also times out with no speech and caps the length.
/// </summary>
public sealed class EndOfSpeechDetector
{
    private readonly EndOfSpeechSettings _s;
    private double _elapsedMs, _speechMs, _silenceMs;

    public EndOfSpeechDetector(EndOfSpeechSettings? settings = null) => _s = settings ?? new EndOfSpeechSettings();

    public EndOfSpeechState State { get; private set; } = EndOfSpeechState.WaitingForSpeech;
    public bool IsDone => State is EndOfSpeechState.Ended or EndOfSpeechState.NoSpeech or EndOfSpeechState.MaxLength;

    public void Reset()
    {
        State = EndOfSpeechState.WaitingForSpeech;
        _elapsedMs = _speechMs = _silenceMs = 0;
    }

    /// <summary>Feed one buffer of 16 kHz -1..1 audio; returns the new state.</summary>
    public EndOfSpeechState Process(ReadOnlySpan<float> audio, int sampleRate = 16000)
    {
        if (IsDone || audio.IsEmpty) return State;
        var ms = audio.Length * 1000.0 / sampleRate;
        _elapsedMs += ms;
        var speech = Stt.UtteranceBuffer.Rms(audio) >= _s.SpeechRms;

        if (speech)
        {
            _speechMs += ms;
            _silenceMs = 0;
            if (State == EndOfSpeechState.WaitingForSpeech) State = EndOfSpeechState.Speaking;
        }
        else if (State == EndOfSpeechState.Speaking)
        {
            _silenceMs += ms;
            if (_silenceMs >= _s.EndSilenceMs)
            {
                if (_speechMs >= _s.MinSpeechMs) State = EndOfSpeechState.Ended;
                else { State = EndOfSpeechState.WaitingForSpeech; _speechMs = 0; _silenceMs = 0; } // just a click
            }
        }

        if (State == EndOfSpeechState.WaitingForSpeech && _elapsedMs >= _s.NoSpeechTimeoutMs) State = EndOfSpeechState.NoSpeech;
        else if (!IsDone && _elapsedMs >= _s.MaxUtteranceMs) State = EndOfSpeechState.MaxLength;
        return State;
    }
}
