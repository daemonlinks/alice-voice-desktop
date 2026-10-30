namespace AliceVoice.Core.Stt;

/// <summary>
/// Collects 16 kHz mono float frames while Talk is held, then hands the whole
/// utterance to STT once. Thread-safe (frames arrive on the audio thread).
/// </summary>
public sealed class UtteranceBuffer
{
    public const int SampleRate = 16000;
    private readonly List<float> _samples = new();
    private readonly object _lock = new();
    private readonly int _minSamples;
    private readonly int _maxSamples;
    private bool _recording;

    public UtteranceBuffer(double minSeconds = 0.3, double maxSeconds = 120)
    {
        _minSamples = (int)(minSeconds * SampleRate);
        _maxSamples = (int)(maxSeconds * SampleRate);
    }

    public bool IsRecording { get { lock (_lock) return _recording; } }
    public double Seconds { get { lock (_lock) return _samples.Count / (double)SampleRate; } }

    /// <summary>True once the cap was hit (further audio is dropped).</summary>
    public bool IsFull { get { lock (_lock) return _samples.Count >= _maxSamples; } }

    public void Start()
    {
        lock (_lock) { _samples.Clear(); _recording = true; }
    }

    /// <summary>Append a frame (ignored when not recording). Returns the frame RMS (0..1) for the VU meter.</summary>
    public float Append(ReadOnlySpan<float> frame)
    {
        float rms = Rms(frame);
        lock (_lock)
        {
            if (!_recording) return rms;
            var room = _maxSamples - _samples.Count;
            if (room <= 0) return rms;
            var take = Math.Min(room, frame.Length);
            for (int i = 0; i < take; i++) _samples.Add(frame[i]);
        }
        return rms;
    }

    /// <summary>
    /// Stop recording and return the utterance as Int16 PCM, or null when it
    /// is shorter than the minimum (an accidental tap).
    /// </summary>
    public short[]? Stop()
    {
        lock (_lock)
        {
            _recording = false;
            if (_samples.Count < _minSamples) { _samples.Clear(); return null; }
            var pcm = new short[_samples.Count];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)Math.Clamp(_samples[i] * 32767f, short.MinValue, short.MaxValue);
            _samples.Clear();
            return pcm;
        }
    }

    public void Cancel()
    {
        lock (_lock) { _recording = false; _samples.Clear(); }
    }

    public static float Rms(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return (float)Math.Sqrt(sum / frame.Length);
    }
}
