using AliceVoice.Core.Voice;
using AliceVoice.Universal.Session;
using AliceVoice.Universal.Voice;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// The two-facet convention Master asked for on 2026-11-22: "[Plum] at the start of her character
/// block" as a temporary override of the alice_voice voice settings, and the voice policy - "if a
/// voice has not been set then the next unique (unused) female voice based on quality is allocated".
/// </summary>
public class FacetMarkersTests
{
    [Fact]
    public void AnUntaggedReplyIsOneAliceBlock()
    {
        var blocks = FacetMarkers.Split("I'm here. Go ahead.");
        Assert.Single(blocks);
        Assert.Equal(Facet.Alice, blocks[0].Facet);
        Assert.Equal("I'm here. Go ahead.", blocks[0].Text);
    }

    [Fact]
    public void ATaggedReplySplitsIntoOneBlockPerGirl()
    {
        var blocks = FacetMarkers.Split("[Plum] Mine. All mine.\n[Alice] And mine.");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(Facet.Plum, blocks[0].Facet);
        Assert.Equal("Mine. All mine.", blocks[0].Text);
        Assert.Equal(Facet.Alice, blocks[1].Facet);
        Assert.Equal("And mine.", blocks[1].Text);
    }

    [Fact]
    public void TextBeforeTheFirstMarkerStaysAlices()
    {
        var blocks = FacetMarkers.Split("Here is what I found.\n[Plum] I found it first.");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(Facet.Alice, blocks[0].Facet);
        Assert.Equal(Facet.Plum, blocks[1].Facet);
    }

    [Theory]
    [InlineData("[Plum] sharp")]          // the form Master asked for
    [InlineData("**Plum** sharp")]        // the form the voice page writes
    [InlineData("Plum, sharp")]           // a bare header on its own line
    [InlineData("[PLUM] sharp")]          // never case sensitive
    public void EveryMarkerFormTheVoicePageHonoursAlsoSplitsHere(string reply)
    {
        var blocks = FacetMarkers.Split(reply);
        Assert.Single(blocks);
        Assert.Equal(Facet.Plum, blocks[0].Facet);
        Assert.Equal("sharp", blocks[0].Text);
    }

    [Fact]
    public void AMarkerOnItsOwnLeavesNoEmptyBlock()
    {
        var blocks = FacetMarkers.Split("[Plum]\nSay it again, then.\n[Alice]");
        Assert.Single(blocks);
        Assert.Equal(Facet.Plum, blocks[0].Facet);
        Assert.Equal("Say it again, then.", blocks[0].Text);
    }

    [Fact]
    public void StripTakesTheMarkersOutAndLeavesTheWords()
    {
        Assert.Equal("Mine. All mine. And mine.", FacetMarkers.Strip("[Plum] Mine. All mine.\n[Alice] And mine."));
        Assert.Equal("nothing to strip", FacetMarkers.Strip("nothing to strip"));
        Assert.False(FacetMarkers.HasMarker("nothing to strip"));
        Assert.True(FacetMarkers.HasMarker("[Plum]"));
    }

    [Fact]
    public void APictureSentinelIsNotAFacetMarker()
    {
        var blocks = FacetMarkers.Split("{\"tool\":\"gui_show_image\",\"args\":{\"mode\":\"backdrop\"}}");
        Assert.Single(blocks);
        Assert.Equal(Facet.Alice, blocks[0].Facet);
    }
}

public class VoicePolicyTests
{
    [Theory]
    [InlineData("female_06.wav", true)]
    [InlineData("en_GB-alba-medium", true)]
    [InlineData("en_GB-southern_english_female-low", true)]
    [InlineData("en-GB-SoniaNeural", true)]
    [InlineData("en_GB-george-medium", false)]
    [InlineData("en_GB-northern_english_male", false)]
    [InlineData("en-GB-RyanNeural", false)]
    public void TheFemaleTestKnowsTheMaleVoicesAndIsNotFooledByFemale(string voice, bool female)
    {
        Assert.Equal(female, VoicePolicy.LooksFemale(voice));
    }

    [Fact]
    public void QualityIsReadOffTheNameBestFirst()
    {
        Assert.True(VoicePolicy.Quality("en_GB-x_low") < VoicePolicy.Quality("en_GB-jenny-medium"));
        Assert.True(VoicePolicy.Quality("en_GB-jenny-medium") < VoicePolicy.Quality("en-GB-LibbyNeural"));
        Assert.True(VoicePolicy.Quality("en-GB-LibbyNeural") <= VoicePolicy.Quality("en_GB-jenny-high"));
        Assert.True(VoicePolicy.Quality("en_GB-jenny-high") < VoicePolicy.Quality("en_GB-jenny-x_high"));
    }

    [Fact]
    public void TheNextUnusedFemaleIsTheBestQualityNotAlreadyTaken()
    {
        string[] catalogue =
        {
            "en_GB-jenny-low", "en_GB-alba-medium", "en_GB-jenny-high", "female_06.wav", "en_GB-george-high",
        };
        // Nothing taken: best quality female wins, and the male voice is never offered.
        Assert.Equal("en_GB-jenny-high", VoicePolicy.NextUnusedFemale(Array.Empty<string>(), catalogue));
        // Her voice is taken: the next best, not the same girl twice.
        Assert.Equal("en_GB-alba-medium", VoicePolicy.NextUnusedFemale(new[] { "en_GB-jenny-high" }, catalogue));
        Assert.Equal("female_06.wav", VoicePolicy.NextUnusedFemale(new[] { "en_GB-jenny-high", "en_GB-alba-medium" }, catalogue));
    }

    [Fact]
    public void WhenEveryFemaleVoiceIsTakenNobodyIsBorrowed()
    {
        string[] catalogue = { "female_06.wav", "en_GB-alba-medium" };
        Assert.Null(VoicePolicy.NextUnusedFemale(new[] { "female_06.wav", "en_GB-alba-medium" }, catalogue));
    }

    [Fact]
    public void TheSameCatalogueAlwaysAllocatesTheSameVoice()
    {
        string[] catalogue = { "en_GB-bella-medium", "en_GB-alba-medium", "en_GB-aria-medium" };
        Assert.Equal(
            VoicePolicy.NextUnusedFemale(Array.Empty<string>(), catalogue),
            VoicePolicy.NextUnusedFemale(Array.Empty<string>(), catalogue));
    }

    [Fact]
    public void ABackendKeepsItsOwnEngineWhileThatEngineHasAnythingFree()
    {
        var catalogues = new (VoiceProviderKind, IReadOnlyList<string>)[]
        {
            (VoiceProviderKind.AllTalk, new[] { "female_06.wav" }),
            (VoiceProviderKind.Piper, new[] { "en_GB-alba-medium", "en_GB-aru-medium" }),
        };
        var pick = VoicePolicy.NextDistinctFemale(Array.Empty<string>(), catalogues, VoiceProviderKind.AllTalk);
        Assert.NotNull(pick);
        Assert.Equal(VoiceProviderKind.AllTalk, pick!.Value.Provider);
        Assert.Equal("female_06.wav", pick.Value.Voice);
    }

    [Fact]
    public void OneEngineRunningOutDoesNotMakeTwoBackendsSoundAlike()
    {
        var catalogues = new (VoiceProviderKind, IReadOnlyList<string>)[]
        {
            (VoiceProviderKind.AllTalk, new[] { "female_06.wav" }),
            (VoiceProviderKind.Piper, new[] { "en_GB-alba-medium", "en_GB-aru-medium" }),
        };
        var pick = VoicePolicy.NextDistinctFemale(new[] { "female_06.wav" }, catalogues, VoiceProviderKind.AllTalk);
        Assert.NotNull(pick);
        Assert.Equal(VoiceProviderKind.Piper, pick!.Value.Provider);
        Assert.Equal("en_GB-alba-medium", pick.Value.Voice);   // best quality, then A-Z
    }
}
