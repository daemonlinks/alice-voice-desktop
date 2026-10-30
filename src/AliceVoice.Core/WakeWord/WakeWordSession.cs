namespace AliceVoice.Core.WakeWord;

public enum WakeWordSessionState { Paused, Listening, Capturing }

/// <summary>Settings for hands-free listening.</summary>
public sealed record WakeWordSessionSettings
{
    public float Threshold { get; init; } = 0.5f;
    public int Patience { get; init; } = 1;

    /// <summary>Debounce after a detection, in 80 ms frames.</summary>
    public int RefractoryFrames { get; init; } = 25;

    /// <summary>Audio ignored after <see cref="WakeWordSession.Resume"/> (lets the room echo of Alice's voice die away).</summary>
    public int ResumeGuardMs { get; init; } = 600;

    public EndOfSpeechSettings EndOfSpeech { get; init; } = new();
}

/// <summary>
/// Hands-free listening state machine. Listening: audio goes to the wake-word
/// detector. On a detection it switches to Capturing and records the command
/// until end of speech (energy VAD), then raises <see cref="UtteranceReady"/>
/// and pauses itself: the app resumes it once the reply has been spoken, so
/// Alice's own voice never reaches the detector. Thread-safe: audio may be fed
/// from a worker while the UI pauses/resumes.
/// </summary>
public sealed class WakeWordSession : IDisposable
{
    public const int SampleRate = 16000;

    private readonly IWakeWordDetector _detector;
    private readonly WakeWordTrigger _trigger;
    private readonly EndOfSpeechDetector _eos;
    private readonly WakeWordSessionSettings _settings;
    private readonly object _lock = new();
    private readonly List<float> _utterance = new();
    private int _guardSamples;

    public WakeWordSession(IWakeWordDetector detector, WakeWordSessionSettings? settings = null)
    {
        _detector = detector;
        _settings = settings ?? new WakeWordSessionSettings();
        _trigger = new WakeWordTrigger(_settings.Threshold, _settings.Patience, _settings.RefractoryFrames);
        _eos = new EndOfSpeechDetector(_settings.EndOfSpeech);
    }

    /// <summary>Raised (on the feeding thread) with the score of the detection.</summary>
    public event Action<float>? WakeDetected;

    /// <summary>Raised with the command as Int16 PCM; the session is Paused afterwards.</summary>
    public event Action<short[]>? UtteranceReady;

    /// <summary>Raised when nothing was said after the wake word (back to Listening).</summary>
    public event Action<EndOfSpeechState>? UtteranceAbandoned;

    public string ModelName => _detector.ModelName;
    public WakeWordSessionState State { get { lock (_lock) return _state; } }
    private WakeWordSessionState _state = WakeWordSessionState.Paused;

    /// <summary>Latest frame score (for a UI meter).</summary>
    public float LastScore { get; private set; }

    public float Threshold
    {
        get { lock (_lock) return _trigger.Threshold; }
        set { lock (_lock) _trigger.Threshold = value; }
    }

    /// <summary>Start (or restart) listening for the wake word with fresh detector state.</summary>
    public void Resume(int? guardMs = null)
    {
        lock (_lock)
        {
            _detector.Reset();
            _trigger.Reset();
            _utterance.Clear();
            _guardSamples = (guardMs ?? _settings.ResumeGuardMs) * SampleRate / 1000;
            LastScore = 0;
            _state = WakeWordSessionState.Listening;
        }
    }

    /// <summary>Stop reacting to audio (Alice speaking, push-to-talk, mode off).</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _state = WakeWordSessionState.Paused;
            _utterance.Clear();
        }
    }

    /// <summary>Feed 16 kHz mono -1..1 audio.</summary>
    public void Process(ReadOnlySpan<float> audio)
    {
        float detectedScore = -1;
        short[]? ready = null;
        EndOfSpeechState? abandoned = null;

        lock (_lock)
        {
            if (_state == WakeWordSessionState.Paused || audio.IsEmpty) return;
            if (_guardSamples > 0)
            {
                var skip = Math.Min(_guardSamples, audio.Length);
                _guardSamples -= skip;
                audio = audio[skip..];
                if (audio.IsEmpty) return;
            }

            if (_state == WakeWordSessionState.Listening)
            {
                foreach (var score in _detector.Process(audio))
                {
                    LastScore = score;
                    if (_trigger.Update(score))
                    {
                        detectedScore = score;
                        _state = WakeWordSessionState.Capturing;
                        _utterance.Clear();
                        _eos.Reset();
                        break; // the command starts with the next buffer
                    }
                }
            }
            else if (_state == WakeWordSessionState.Capturing)
            {
                foreach (var s in audio) _utterance.Add(s);
                var st = _eos.Process(audio, SampleRate);
                if (st is EndOfSpeechState.Ended or EndOfSpeechState.MaxLength)
                {
                    ready = new short[_utterance.Count];
                    for (int i = 0; i < ready.Length; i++)
                        ready[i] = (short)Math.Clamp(_utterance[i] * 32767f, short.MinValue, short.MaxValue);
                    _utterance.Clear();
                    _state = WakeWordSessionState.Paused;
                }
                else if (st == EndOfSpeechState.NoSpeech)
                {
                    abandoned = st;
                    _utterance.Clear();
                    _detector.Reset();
                    _trigger.Reset();
                    _state = WakeWordSessionState.Listening;
                }
            }
        }

        // Raise outside the lock so handlers may call Pause/Resume.
        if (detectedScore >= 0) WakeDetected?.Invoke(detectedScore);
        if (ready is not null) UtteranceReady?.Invoke(ready);
        if (abandoned is { } a) UtteranceAbandoned?.Invoke(a);
    }

    public void Dispose() => _detector.Dispose();
}
