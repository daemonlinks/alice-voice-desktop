using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

public class ToolCommandParserTests
{
    [Fact]
    public void BareCommandIsParsedAndNothingIsLeftToSpeak()
    {
        var p = ToolCommandParser.Parse("{\"tool\":\"gui_show_image\",\"args\":{\"src\":\"C:\\\\x\\\\a.png\"}}");
        Assert.Single(p.Commands);
        Assert.Equal("gui_show_image", p.Commands[0].Tool);
        Assert.Contains("a.png", p.Commands[0].ArgsJson);
        Assert.Equal("", p.Prose);
    }

    [Fact]
    public void ProseAroundTheCommandIsKeptAndTheJsonIsNot()
    {
        var p = ToolCommandParser.Parse("Here you go, Master. {\"tool\":\"gui_show_image\",\"args\":{\"src\":\"a.png\",\"caption\":\"a } brace\"}} Enjoy.");
        Assert.Single(p.Commands);
        Assert.Equal("Here you go, Master. Enjoy.", p.Prose);
        Assert.DoesNotContain("{", p.Prose);
        Assert.DoesNotContain("tool", p.Prose);
    }

    [Fact]
    public void FencedCommandLeavesNoEmptyFence()
    {
        var p = ToolCommandParser.Parse("Look:\n```json\n{\"tool\":\"gui_image_clear\",\"args\":{}}\n```\nGone.");
        Assert.Single(p.Commands);
        Assert.DoesNotContain("```", p.Prose);
        Assert.Contains("Look:", p.Prose);
        Assert.Contains("Gone.", p.Prose);
    }

    [Fact]
    public void UnclosedFragmentIsCutAndNotSpoken()
    {
        var p = ToolCommandParser.Parse("One moment {\"tool\":\"gui_show_image\",\"args\":{\"src\":\"a.p");
        Assert.Empty(p.Commands);
        Assert.Equal("One moment", p.Prose);
    }

    [Fact]
    public void OrdinaryBracesWithoutToolAreLeftAlone()
    {
        var text = "Set {\"a\":1} and {curly} stays";
        Assert.Equal(text, ToolCommandParser.Parse(text).Prose);
        Assert.False(ToolCommandParser.HoldsToolCall(text));
    }

    [Fact]
    public void FilterRunsOnlyAllowlistedDisplayCommands()
    {
        var f = DisplayCommands.Filter("Hi {\"tool\":\"gui_show_image\",\"args\":{\"src\":\"a.png\"}} {\"tool\":\"gui_mood\",\"args\":{\"mood\":\"x\"}} {\"tool\":\"gui_reset\",\"args\":{}} {\"tool\":\"gui_image_clear\",\"args\":{}}");
        Assert.Equal(new[] { "gui_show_image", "gui_image_clear" }, f.Display.Select(c => c.Tool));
        Assert.Equal(new[] { "gui_mood", "gui_reset" }, f.Ignored);
        Assert.Equal("Hi", f.Prose);
    }

    [Fact]
    public void DisplayEventCarriesToolAndArgs()
    {
        var evt = DisplayCommands.ToEvent(new ToolCommand("gui_show_image", "{\"src\":\"a.png\"}"));
        Assert.Equal(DisplayCommands.EventType, evt.Type);
        Assert.Equal("gui_show_image", evt.Payload!["tool"]!.GetValue<string>());
        Assert.Equal("a.png", evt.Payload["args"]!["src"]!.GetValue<string>());
    }

    [Fact]
    public void ParsingIsCheapEnoughToAddNoTurnLatency()
    {
        var reply = string.Concat(Enumerable.Repeat("Some words of a reply. ", 200)) + "{\"tool\":\"gui_show_image\",\"args\":{\"src\":\"a.png\"}}";
        ToolCommandParser.Parse(reply);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) ToolCommandParser.Parse(reply);
        Assert.True(sw.Elapsed.TotalMilliseconds / 100 < 5, $"parse took {sw.Elapsed.TotalMilliseconds / 100:0.000} ms");
    }
}
