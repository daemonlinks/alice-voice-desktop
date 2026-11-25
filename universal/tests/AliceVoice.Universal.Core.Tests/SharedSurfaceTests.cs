using System.Text.Json.Nodes;
using AliceVoice.Universal.Interaction;
using AliceVoice.Universal.Media;
using AliceVoice.Universal.Session;
using AliceVoice.Universal.Tools;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// The middle of the conversation screen, and the tools that drive it. These pin the contract the
/// agent relies on: what a kind maps to, what a refusal says, and that the meters keep his value
/// instead of snapping back under his finger.
/// </summary>
public class SharedSurfaceTests
{
    private static AppToolHost Host(SharedSurface? surface = null, MediaSettings? settings = null)
    {
        settings ??= MediaSettings.Default;
        var share = new MediaShare(settings);
        var display = new ImageDisplay(settings, share, null, _ => { });
        return new AppToolHost(display, surface);
    }

    private static string Run(AppToolHost host, string tool, string json)
        => host.RunAsync(tool, json).GetAwaiter().GetResult() ?? "null";

    [Fact]
    public void TheSurfaceToolsBelongToTheApp()
    {
        Assert.True(AppToolHost.Owns("gui_surface"));
        Assert.True(AppToolHost.Owns("gui_surface_clear"));
        Assert.False(AppToolHost.Owns("alice_local_ask"));
    }

    [Fact]
    public void TheHostPaintsTheSurfaceItWasGiven()
    {
        var surface = new SharedSurface();
        Assert.Same(surface, Host(surface).SurfaceArea);
    }

    [Fact]
    public void AQuestionArrivesWithItsAnswers()
    {
        var surface = new SharedSurface();
        var result = Run(Host(surface), "gui_surface",
            """{"kind":"question","title":"Tea or me?","options":["tea","you"],"by":"alice_voice"}""");
        var json = JsonNode.Parse(result)!.AsObject();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal(2, json["options"]!.GetValue<int>());
        Assert.Equal(SurfaceKind.Question, surface.Kind);
        Assert.Equal("Tea or me?", surface.Title);
        Assert.Equal("alice_voice", surface.By);
        Assert.Equal(new[] { "tea", "you" }, surface.Options.Select(o => o.Text));
    }

    [Fact]
    public void OptionsMayCarryAValueOfTheirOwn()
    {
        var surface = new SharedSurface();
        Run(Host(surface), "gui_surface",
            """{"kind":"question","options":[{"text":"slower","value":10},{"text":"now","value":90}]}""");
        Assert.Equal(10, surface.Options[0].Value);
        Assert.Equal(90, surface.Options[1].Value);
    }

    [Fact]
    public void AQuestionWithNoAnswersIsRefusedWithAReason()
    {
        var surface = new SharedSurface();
        var result = Run(Host(surface), "gui_surface", """{"kind":"question","title":"Well?"}""");
        Assert.Contains("bad args", result);
        Assert.False(surface.HasContent);
    }

    [Fact]
    public void AnUnknownKindIsRefusedAndNamesTheOnesThatWork()
    {
        var surface = new SharedSurface();
        var result = Run(Host(surface), "gui_surface", """{"kind":"hologram","src":"x"}""");
        Assert.Contains("image|video|question|status|meters", result);
        Assert.False(surface.HasContent);
    }

    [Fact]
    public void AnImageOutsideTheAllowedRootsIsRefused()
    {
        var surface = new SharedSurface();
        var result = Run(Host(surface), "gui_surface", """{"kind":"image","src":"C:\\Windows\\win.ini"}""");
        Assert.Contains("error", result);
        Assert.False(surface.HasContent);
    }

    [Fact]
    public void AMeterKeepsWhatHeDragged()
    {
        var surface = new SharedSurface();
        Run(Host(surface), "gui_surface", """{"kind":"meters","love":20,"orgasm":10}""");
        Assert.Equal(20, surface.Love);
        Assert.Equal(10, surface.Orgasm);
        surface.Nudge(love: 88);
        Assert.Equal(88, surface.Love);
        Assert.Equal(10, surface.Orgasm);
    }

    [Fact]
    public void MetersAreClampedToTheHundredItShows()
    {
        var surface = new SharedSurface();
        Run(Host(surface), "gui_surface", """{"kind":"meters","love":900,"orgasm":-4}""");
        Assert.Equal(100, surface.Love);
        Assert.Equal(0, surface.Orgasm);
        surface.Nudge(orgasm: 250);
        Assert.Equal(100, surface.Orgasm);
    }

    [Fact]
    public void TheSignatureChangesOnlyWhenTheSurfaceDoes()
    {
        var surface = new SharedSurface();
        var empty = surface.Signature();
        Assert.Equal(empty, surface.Signature());
        Run(Host(surface), "gui_surface", """{"kind":"status","body":"warm and slow","mood":"loved"}""");
        Assert.NotEqual(empty, surface.Signature());
        var after = surface.Signature();
        Assert.Equal(after, surface.Signature());
        Assert.Equal("loved", surface.Mood);
    }

    [Fact]
    public void ClearingItEmptiesTheMiddleAndSaysWhatItDid()
    {
        var surface = new SharedSurface();
        var host = Host(surface);
        Run(host, "gui_surface", """{"kind":"meters","love":50}""");
        var cleared = JsonNode.Parse(Run(host, "gui_surface_clear", "{}"))!.AsObject();
        Assert.True(cleared["cleared"]!.GetValue<bool>());
        Assert.False(surface.HasContent);
        Assert.Null(surface.Love);
        var again = JsonNode.Parse(Run(host, "gui_surface_clear", "{}"))!.AsObject();
        Assert.False(again["cleared"]!.GetValue<bool>());
    }

    [Fact]
    public void WhatAPhoneReadsCarriesNoBytesOnlyWhereTheyAre()
    {
        var surface = new SharedSurface();
        Run(Host(surface), "gui_surface",
            """{"kind":"status","src":"","body":"warm and slow","mood":"loved","love":42}""");
        var json = surface.ToJson();
        Assert.Equal("status", json["kind"]!.GetValue<string>());
        Assert.True(json["has_content"]!.GetValue<bool>());
        Assert.Equal("loved", json["mood"]!.GetValue<string>());
        Assert.Equal(42, json["love"]!.GetValue<double>());
        Assert.Empty(json["options"]!.AsArray());
    }

    [Fact]
    public void AVideoOutsideWhatThisDeviceMayShowIsRefused()
    {
        var surface = new SharedSurface();
        var result = Run(Host(surface), "gui_surface",
            """{"kind":"video","src":"https://example.invalid/a.mp4"}""");
        Assert.Contains("error", result);
        Assert.False(surface.HasContent);
    }

    [Fact]
    public void AVideoIsAllowedWhenTheSettingsAllowHttps()
    {
        var surface = new SharedSurface();
        var settings = MediaSettings.Default with { Sources = MediaSources.AllowedRootsAndHttps };
        var result = Run(Host(surface, settings), "gui_surface",
            """{"kind":"video","src":"https://example.invalid/a.mp4","title":"for you"}""");
        Assert.Contains("\"ok\":true", result.Replace(" ", ""));
        Assert.Equal(SurfaceKind.Video, surface.Kind);
        Assert.Equal("https://example.invalid/a.mp4", surface.Src);
    }

    [Fact]
    public void TheTwoSwitchesSurviveARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appu-prefs-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, """{"BeastHost":"100.64.237.3","BridgePort":8765}""");
        try
        {
            var prefs = UiPreferences.Load(file);
            Assert.True(prefs.SpeakReplies);
            Assert.True(prefs.FollowConversation);
            prefs.Save(speakReplies: false, followConversation: true);
            var again = UiPreferences.Load(file);
            Assert.False(again.SpeakReplies);
            Assert.True(again.FollowConversation);
            // The bridge address it shares the file with is left exactly as it was.
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.Equal("100.64.237.3", json["BeastHost"]!.GetValue<string>());
            Assert.Equal(8765, json["BridgePort"]!.GetValue<int>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ABrokenSettingsFileStillGivesHimASpeakingGirl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appu-prefs-bad-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        File.WriteAllText(file, "{ this is not json");
        try
        {
            var prefs = UiPreferences.Load(file);
            Assert.True(prefs.SpeakReplies);
            Assert.True(prefs.FollowConversation);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
