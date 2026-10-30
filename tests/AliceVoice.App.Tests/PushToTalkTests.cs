using AliceVoice.Core.Stt;
using Xunit;

namespace AliceVoice.App.Tests;

public class PushToTalkGestureTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HoldThenReleaseTranscribesOnce()
    {
        var g = new PushToTalkGesture();
        Assert.Equal(PttAction.StartRecording, g.Press(T0));
        Assert.Equal(PttAction.StopAndTranscribe, g.Release(T0.AddSeconds(2)));
        Assert.Equal(PushToTalkGesture.State.Idle, g.Current);
        Assert.Equal(PttAction.None, g.Release(T0.AddSeconds(3))); // stray release does nothing
    }

    [Fact]
    public void QuickTapLatchesThenTapSends()
    {
        var g = new PushToTalkGesture();
        g.Press(T0);
        Assert.Equal(PttAction.None, g.Release(T0.AddMilliseconds(100)));
        Assert.Equal(PushToTalkGesture.State.Latched, g.Current);
        Assert.Equal(PttAction.StopAndTranscribe, g.Press(T0.AddSeconds(5)));
        Assert.Equal(PttAction.None, g.Release(T0.AddSeconds(5.1)));
        Assert.Equal(PushToTalkGesture.State.Idle, g.Current);
    }

    [Fact]
    public void CancelWhileHoldingStops()
    {
        var g = new PushToTalkGesture();
        g.Press(T0);
        Assert.Equal(PttAction.StopAndTranscribe, g.Cancel());
        Assert.Equal(PttAction.None, g.Cancel());
    }

    [Fact]
    public void KeyboardToggle()
    {
        var g = new PushToTalkGesture();
        Assert.Equal(PttAction.StartRecording, g.Toggle());
        Assert.Equal(PttAction.StopAndTranscribe, g.Toggle());
    }
}

public class UtteranceBufferTests
{
    private static float[] Tone(int samples, float amp = 0.5f) => Enumerable.Repeat(amp, samples).ToArray();

    [Fact]
    public void CollectsOnlyWhileRecording()
    {
        var b = new UtteranceBuffer();
        b.Append(Tone(16000));
        b.Start();
        b.Append(Tone(16000));
        var pcm = b.Stop();
        Assert.NotNull(pcm);
        Assert.Equal(16000, pcm!.Length);
        Assert.Equal((short)(0.5f * short.MaxValue), pcm[0]);
        Assert.False(b.IsRecording);
    }

    [Fact]
    public void TooShortReturnsNull()
    {
        var b = new UtteranceBuffer(minSeconds: 0.3);
        b.Start();
        b.Append(Tone(1600)); // 0.1 s
        Assert.Null(b.Stop());
    }

    [Fact]
    public void CapsAtMax()
    {
        var b = new UtteranceBuffer(minSeconds: 0.1, maxSeconds: 1);
        b.Start();
        for (int i = 0; i < 5; i++) b.Append(Tone(8000));
        Assert.True(b.IsFull);
        Assert.Equal(16000, b.Stop()!.Length);
    }

    [Fact]
    public void RmsOfConstant()
    {
        Assert.Equal(0.5f, UtteranceBuffer.Rms(Tone(100)), 3);
        Assert.Equal(0f, UtteranceBuffer.Rms(ReadOnlySpan<float>.Empty));
    }
}
