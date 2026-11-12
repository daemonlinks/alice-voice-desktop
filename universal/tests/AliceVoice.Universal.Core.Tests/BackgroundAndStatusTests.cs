using AliceVoice.Universal.Session;
using AliceVoice.Universal.Voice;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class BackgroundAndStatusTests
{
    [Fact]
    public void WideImageInATallViewIsLetterboxed()
    {
        var placed = BackgroundFit.Place(1920, 1080, 1080, 1920);
        Assert.Equal(1080, placed.Width, 3);
        Assert.True(placed.Height < 1920);
        Assert.True(placed.Y > 0);
        Assert.Equal(0, placed.X, 3);
        AssertPreserved(1920, 1080, placed);
        AssertInside(placed, 1080, 1920);
    }

    [Fact]
    public void TallImageInAWideViewIsPillarboxed()
    {
        var placed = BackgroundFit.Place(1080, 1920, 1920, 1080);
        Assert.Equal(1080, placed.Height, 3);
        Assert.True(placed.Width < 1920);
        Assert.True(placed.X > 0);
        Assert.Equal(0, placed.Y, 3);
        AssertPreserved(1080, 1920, placed);
        AssertInside(placed, 1920, 1080);
    }

    [Fact]
    public void MatchingAspectFillsTheView()
    {
        var placed = BackgroundFit.Place(1600, 900, 1920, 1080);
        Assert.Equal(0, placed.X, 3);
        Assert.Equal(0, placed.Y, 3);
        Assert.Equal(1920, placed.Width, 3);
        Assert.Equal(1080, placed.Height, 3);
    }

    [Fact]
    public void ResizeRefitsWithoutCropping()
    {
        var small = BackgroundFit.Place(1000, 500, 800, 600);
        var large = BackgroundFit.Place(1000, 500, 2000, 2000);
        Assert.NotEqual(small.Width, large.Width);
        AssertPreserved(1000, 500, small);
        AssertPreserved(1000, 500, large);
        AssertInside(small, 800, 600);
        AssertInside(large, 2000, 2000);
    }

    [Fact]
    public void EmptyImagePlacesNothing()
    {
        var placed = BackgroundFit.Place(0, 1080, 1920, 1080);
        Assert.Equal(0, placed.Width);
        Assert.Equal(0, placed.Height);
    }

    [Fact]
    public void PageUsesAspectFitAndDoesNotCrop()
    {
        Assert.Equal("AspectFit", BackgroundFit.Aspect);
        var xaml = File.ReadAllText(RepoFile("universal/src/AliceVoice.Universal.App/MainPage.xaml"));
        var code = File.ReadAllText(RepoFile("universal/src/AliceVoice.Universal.App/MainPage.xaml.cs"));
        Assert.Contains("Aspect=\"AspectFit\"", xaml);
        Assert.Contains("Aspect.AspectFit", code);
        Assert.DoesNotContain("Aspect=\"AspectFill\"", xaml);
        Assert.DoesNotContain("Aspect.AspectFill", code);
        Assert.DoesNotContain("Aspect=\"Fill\"", xaml);
        Assert.DoesNotContain("UniformToFill", xaml + code);
        Assert.DoesNotContain("BackgroundImageSource", xaml + code);
    }

    [Theory]
    [InlineData("Hello Master", null, "Hello Master")]
    [InlineData("Hello Master", "", "Hello Master")]
    [InlineData("Hello Master", "Master", "Master")]
    [InlineData(null, null, "")]
    public void CopyUsesTheHighlightWhenThereIsOne(string? message, string? selected, string expected)
        => Assert.Equal(expected, TranscriptCopy.Text(message, selected));

    [Fact]
    public void ConversationMessagesAreSelectableLabels()
    {
        var page = File.ReadAllText(RepoFile("universal/src/AliceVoice.Universal.App/MainPage.xaml.cs"));
        var selection = File.ReadAllText(RepoFile("universal/src/AliceVoice.Universal.App/SelectableTranscript.cs"));
        var scene = File.ReadAllText(RepoFile("universal/src/AliceVoice.Universal.App/MainPage.xaml"));
        Assert.Contains("new SelectableLabel", page);
        Assert.Contains("MessageText(entry.Text", page);
        Assert.DoesNotContain("new Label { Text = entry.Text", page);
        Assert.Contains("IsTextSelectionEnabled = true", selection);
        Assert.Contains("SetTextIsSelectable(true)", selection);
        Assert.Contains("TranscriptCopy.Text", selection);
        Assert.Contains("InputTransparent=\"True\"", scene);
        Assert.Equal("Copy", TranscriptCopy.Action);
    }

    [Fact]
    public void LampsUseGreenAmberAndRedForLiveState()
    {
        Assert.Equal(StatusTone.Green, StatusTones.Presence(connected: true, "Ready"));
        Assert.Equal(StatusTone.Amber, StatusTones.Presence(connected: false, "Connecting…"));
        Assert.Equal(StatusTone.Red, StatusTones.Presence(connected: false, "Ready"));
        Assert.Equal(StatusTone.Red, StatusTones.Presence(connected: false, "Offline"));

        Assert.Equal(StatusTone.Amber, StatusTones.Activity("Listening…", connected: true));
        Assert.Equal(StatusTone.Amber, StatusTones.Activity("Connecting…", connected: false));
        Assert.Equal(StatusTone.Amber, StatusTones.Activity("Thinking…", connected: true));
        Assert.Equal(StatusTone.Amber, StatusTones.Activity("Speaking…", connected: true));
        Assert.Equal(StatusTone.Green, StatusTones.Activity("Ready", connected: true));
        Assert.Equal(StatusTone.Red, StatusTones.Activity("Ready", connected: false));

        Assert.Equal(StatusTone.Amber, StatusTones.Mic(StageLamp.Green, StageLamp.Green, "Listening…"));
        Assert.Equal(StatusTone.Red, StatusTones.Mic(StageLamp.Dim, StageLamp.Red, "Ready"));
        Assert.Equal(StatusTone.Green, StatusTones.Mic(StageLamp.Green, StageLamp.Green, "Ready"));

        Assert.Equal(StatusTone.Amber, StatusTones.Pipeline(StageLamp.Green, "Speaking…", "Speaking…"));
        Assert.Equal(StatusTone.Green, StatusTones.Pipeline(StageLamp.Green, "Speaking…", "Transcribing…"));
        Assert.Equal(StatusTone.Red, StatusTones.ForStage(StageLamp.Red));
        Assert.Equal(StatusTone.Amber, StatusTones.ForStage(StageLamp.Wait));
        Assert.Equal(StatusTone.Off, StatusTones.ForStage(StageLamp.Dim));

        Assert.Equal(StatusTones.GreenHex, StatusTones.Hex(StatusTone.Green));
        Assert.Equal(StatusTones.AmberHex, StatusTones.Hex(StatusTone.Amber));
        Assert.Equal(StatusTones.RedHex, StatusTones.Hex(StatusTone.Red));
        Assert.Contains("What time is it?", SessionCopy.HelpTriggers);
        Assert.Contains("time", SessionCopy.HelpTools);
        Assert.Contains("workspace hermes", SessionCopy.HelpHoncho);
        Assert.Contains("Peer alice_local", SessionCopy.HelpHoncho);
        Assert.Contains("Not a root workspace", SessionCopy.HelpHoncho);
        Assert.Contains("stray workspace id alice_local", SessionCopy.HelpHoncho);
    }

    private static void AssertPreserved(double imageWidth, double imageHeight, BackgroundFit.Placement placed)
        => Assert.Equal(imageWidth / imageHeight, placed.Width / placed.Height, 5);

    private static void AssertInside(BackgroundFit.Placement placed, double viewWidth, double viewHeight)
    {
        Assert.True(placed.Width <= viewWidth + 0.001);
        Assert.True(placed.Height <= viewHeight + 0.001);
        Assert.True(placed.X >= -0.001 && placed.Y >= -0.001);
        Assert.True(Math.Abs(placed.Width - viewWidth) < 0.001 || Math.Abs(placed.Height - viewHeight) < 0.001);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
