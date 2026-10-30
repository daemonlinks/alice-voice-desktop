namespace AliceVoice.Core.WakeWord;

/// <summary>
/// Turns per-frame scores into detections: a score must be at or above
/// <see cref="Threshold"/> for <see cref="Patience"/> consecutive 80 ms frames,
/// and after a detection nothing fires again for <see cref="RefractoryFrames"/>
/// frames (debounce).
/// </summary>
public sealed class WakeWordTrigger
{
    private int _above;
    private int _cooldown;

    public WakeWordTrigger(float threshold = 0.5f, int patience = 1, int refractoryFrames = 25)
    {
        Threshold = threshold;
        Patience = patience;
        RefractoryFrames = refractoryFrames;
    }

    private float _threshold;
    public float Threshold
    {
        get => _threshold;
        set => _threshold = Math.Clamp(value, 0.01f, 0.99f);
    }

    private int _patience;
    public int Patience
    {
        get => _patience;
        set => _patience = Math.Max(1, value);
    }

    private int _refractory;
    public int RefractoryFrames
    {
        get => _refractory;
        set => _refractory = Math.Max(0, value);
    }

    /// <summary>Highest score seen since the last <see cref="Reset"/> (diagnostics).</summary>
    public float PeakScore { get; private set; }

    /// <summary>Feed one frame score; true when this frame is a detection.</summary>
    public bool Update(float score)
    {
        if (score > PeakScore) PeakScore = score;
        if (_cooldown > 0)
        {
            _cooldown--;
            _above = 0;
            return false;
        }
        _above = score >= Threshold ? _above + 1 : 0;
        if (_above < Patience) return false;
        _above = 0;
        _cooldown = RefractoryFrames;
        return true;
    }

    public void Reset()
    {
        _above = 0;
        _cooldown = 0;
        PeakScore = 0;
    }
}
