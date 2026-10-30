namespace AliceVoice.Core.WakeWord;

/// <summary>
/// Re-chunks arbitrary-sized audio buffers (the mic delivers ~50 ms) into
/// fixed frames (openWakeWord uses 80 ms = 1280 samples at 16 kHz). Samples
/// are never dropped or reordered; a partial frame waits for the next push.
/// Not thread-safe (one producer).
/// </summary>
public sealed class AudioFrameBuffer
{
    private readonly float[] _pending;
    private int _count;

    public AudioFrameBuffer(int frameSize = OpenWakeWordPipeline.FrameSamples)
    {
        if (frameSize <= 0) throw new ArgumentOutOfRangeException(nameof(frameSize));
        _pending = new float[frameSize];
    }

    public int FrameSize => _pending.Length;

    /// <summary>Samples waiting for the next full frame.</summary>
    public int Pending => _count;

    /// <summary>Append samples; <paramref name="onFrame"/> is called for every completed frame, in order.</summary>
    public void Push(ReadOnlySpan<float> samples, Action<float[]> onFrame)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(_pending.Length - _count, samples.Length);
            samples[..take].CopyTo(_pending.AsSpan(_count));
            _count += take;
            samples = samples[take..];
            if (_count == _pending.Length)
            {
                onFrame((float[])_pending.Clone());
                _count = 0;
            }
        }
    }

    public void Clear() => _count = 0;
}
