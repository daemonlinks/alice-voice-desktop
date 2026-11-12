using AliceVoice.Core.Audio;
using Xunit;

namespace AliceVoice.App.Tests;

public class AllTalkOutputUrlTests
{
    private const string Beast = "http://100.64.237.3:7851";

    [Theory]
    [InlineData("http://127.0.0.1:7851/audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("http://localhost:7851/audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("http://LOCALHOST:7851/audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("http://[::1]:7851/audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("http://127.0.0.1:7851/audio/clip.wav?download=1", "http://100.64.237.3:7851/audio/clip.wav?download=1")]
    public void LoopbackAbsoluteUrlUsesTheClientBaseHost(string returned, string expected)
        => Assert.Equal(expected, AllTalkOutputUrl.Resolve(Beast, returned));

    [Theory]
    [InlineData("/audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("audio/clip.wav", "http://100.64.237.3:7851/audio/clip.wav")]
    [InlineData("  /audio/clip.wav  ", "http://100.64.237.3:7851/audio/clip.wav")]
    public void RelativeUrlJoinsTheBase(string returned, string expected)
        => Assert.Equal(expected, AllTalkOutputUrl.Resolve(Beast + "/", returned));

    [Fact]
    public void LocalBaseKeepsALoopbackDownload()
    {
        const string local = "http://127.0.0.1:7851";
        const string file = "http://127.0.0.1:7851/audio/clip.wav";
        Assert.Equal(file, AllTalkOutputUrl.Resolve(local, file));
        Assert.Equal("http://127.0.0.1:7851/audio/clip.wav", AllTalkOutputUrl.Resolve(local, "/audio/clip.wav"));
    }

    [Fact]
    public void ANonLoopbackAbsoluteUrlIsLeftAlone()
    {
        const string lan = "http://192.168.1.60:7851/audio/clip.wav";
        Assert.Equal(lan, AllTalkOutputUrl.Resolve(Beast, lan));
    }

    [Fact]
    public void MagicDnsBaseReplacesOnlyTheLoopbackHost()
    {
        Assert.Equal(
            "http://ahc-the-beast:7851/audio/clip.wav",
            AllTalkOutputUrl.Resolve("http://ahc-the-beast:7851", "http://127.0.0.1:7851/audio/clip.wav"));
    }
}
