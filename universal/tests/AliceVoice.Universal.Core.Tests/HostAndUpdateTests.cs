using AliceVoice.Core.Config;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Updates;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Updates;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class BeastEndpointTests
{
    [Fact]
    public void HostOnlyStripsSchemeAndPort()
    {
        Assert.Equal("100.64.1.2", BeastServiceUrls.HostOnly("http://100.64.1.2:11434/ignored"));
        Assert.Equal("beast.tailnet", BeastServiceUrls.HostOnly("beast.tailnet"));
    }

    [Fact]
    public void AndroidPointsUnsetServicesAtTheBeastAndKeepsExplicitOnes()
    {
        var env = new Dictionary<string, string?>
        {
            ["ALICEVOICE_BEAST_HOST"] = "100.64.1.2",
            ["ALICEVOICE_OLLAMA_URL"] = "http://custom:11434",
            ["ALICEVOICE_BRIDGE_PORT"] = "9000"
        };
        var host = UniversalHost.Prepare(PlatformHostKind.Android, Layout(), key => env.GetValueOrDefault(key));
        Assert.Equal("http://custom:11434", host.Options.OllamaUrl);
        Assert.Equal("http://100.64.1.2:8643", host.Options.HermesApiUrl);
        Assert.Equal("http://100.64.1.2:7851", host.Options.AllTalkUrl);
        Assert.Equal(9000, host.Remote!.BridgePort);
        Assert.False(host.BridgeEnabled);
    }

    [Fact]
    public void WindowsKeepsLocalServicesAndParsesTheBridgeBind()
    {
        var env = new Dictionary<string, string?>
        {
            ["ALICEVOICE_BEAST_HOST"] = "100.64.1.2",
            ["ALICEVOICE_BRIDGE_BIND"] = "100.64.1.2:8765"
        };
        var host = UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), key => env.GetValueOrDefault(key));
        Assert.Null(host.Options.OllamaUrl);
        Assert.Equal("http://localhost:11434", host.Options.ResolvedOllamaUrl);
        Assert.True(host.BridgeEnabled);
        Assert.Equal("100.64.1.2", host.BridgeBindHost);
        Assert.Equal(8765, host.BridgeBindPort);
    }

    [Fact]
    public void GpuZeroIsNeverTheVoiceGpu()
    {
        var gpus = new[]
        {
            new GpuInfo(0, "render", 11000, 1000, 10000),
            new GpuInfo(1, "voice", 11000, 2000, 9000),
            new GpuInfo(2, "other", 11000, 8000, 3000)
        };
        Assert.Equal(1, NvidiaGpuSelector.Select(gpus)!.Index);
        Assert.Equal(1, NvidiaGpuSelector.Select(gpus, forcedIndex: 0)!.Index);
        Assert.Equal(2, NvidiaGpuSelector.Select(gpus, forcedIndex: 2)!.Index);
        Assert.Null(NvidiaGpuSelector.Select(new[] { new GpuInfo(0, "only", 11000, 1000, 10000) }));
    }

    private static HostLayout Layout() => new(null, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "voices.json"), Path.GetTempPath());
}

public class UpdateOfferTests
{
    [Fact]
    public void WindowsOnlyReportsTheUpdate()
    {
        var offer = UpdateOffer.For(Release("https://example.com/AliceVoice.App.msix"), PlatformHostKind.Windows);
        Assert.Equal("Update available: 0.2.0", offer.Banner);
        Assert.False(offer.CanInstall);
    }

    [Fact]
    public void AndroidRefusesAWindowsPackage()
    {
        var offer = UpdateOffer.For(Release("https://example.com/AliceVoice.App.msix"), PlatformHostKind.Android);
        Assert.Contains("Windows package", offer.Banner);
        Assert.False(offer.CanInstall);
    }

    [Theory]
    [InlineData("https://example.com/a.msix", true)]
    [InlineData("https://example.com/a.MSIXBUNDLE", true)]
    [InlineData("https://example.com/a.apk", false)]
    public void DetectsWindowsPackages(string url, bool windows) => Assert.Equal(windows, UpdateOffer.IsWindowsPackage(url));

    private static ReleaseInfo Release(string url) => new("0.2.0", url, new string('a', 64));
}

public class SmallPortTests
{
    [Fact]
    public void VuMeterMatchesTheDesktopScale()
    {
        Assert.Equal(0, VuMeter.WidthPx(0));
        Assert.Equal(100, VuMeter.WidthPx(1), 3);
    }

    [Fact]
    public void WakePatienceComesFromTheModelUnlessOverridden()
    {
        var model = new AliceVoice.Core.WakeWord.WakeWordModelInfo("d", "m", "e", "c",
            new AliceVoice.Core.WakeWord.WakeWordModelEntry { Id = "hey_alice", Phrase = "hey alice", Threshold = 0.5f, Patience = 3 });
        var settings = AliceVoice.Universal.Wake.WakeWordSetup.Create(new AliceVoiceOptions(), model);
        Assert.Equal(3, settings.Patience);
        Assert.Equal(0.5f, settings.Threshold);
        Assert.Equal(0.5, AliceVoice.Universal.Wake.WakeWordSetup.SensitivityFor(0.5f));
        Assert.Equal(0.2f, AliceVoice.Universal.Wake.WakeWordSetup.ThresholdFor(0.8));

        var forced = AliceVoice.Universal.Wake.WakeWordSetup.Create(new AliceVoiceOptions { WakeWordPatience = 4, WakeWordThreshold = 0.35f }, model);
        Assert.Equal(4, forced.Patience);
        Assert.Equal(0.35f, forced.Threshold);
    }
}
