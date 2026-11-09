using System.Net;
using System.Net.Sockets;
using AliceVoice.Core.Config;
using AliceVoice.Universal.Composition;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class BridgeClientTests
{
    [Fact]
    public void ClientsDialTheTailscaleAddressBeforeTheShortName()
    {
        var hosts = BridgeClientDirectory.Candidates(null);
        Assert.Equal(BridgeClientDirectory.TailscaleHost, hosts[0]);
        Assert.Equal(BridgeBindPolicy.DefaultBeastHost, hosts[1]);

        var configured = BridgeClientDirectory.Candidates("ahc-the-beast");
        Assert.Equal(new[] { "ahc-the-beast", "100.64.237.3" }, configured);
    }

    [Fact]
    public async Task TheFirstHostThatConnectsWinsAndTheErrorNamesEveryFailure()
    {
        var attempts = new List<string>();
        var dial = await BridgeClientDirectory.PickAsync(["ahc-the-beast", "100.64.237.3"], 8765, (host, port, _) =>
        {
            attempts.Add(host + ":" + port);
            return Task.FromResult(host == "100.64.237.3"
                ? new ProbeResult(true, "connected")
                : new ProbeResult(false, "connection refused"));
        }, CancellationToken.None);
        Assert.Equal("100.64.237.3", dial.Host);
        Assert.Equal(["ahc-the-beast:8765"], attempts.Take(1));
        Assert.Equal(["ahc-the-beast:8765 (connection refused)"], dial.Failed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BridgeClientDirectory.PickAsync(
            ["ahc-the-beast", "100.64.237.3"], 8765,
            (host, _, _) => Task.FromResult(new ProbeResult(false, host == "ahc-the-beast" ? "connection refused" : "timed out")),
            CancellationToken.None));
        Assert.Contains("ahc-the-beast:8765 (connection refused)", ex.Message);
        Assert.Contains("100.64.237.3:8765 (timed out)", ex.Message);
    }

    [Fact]
    public void SavingTheClientKeepsTheListenBindAndOtherSettings()
    {
        var json = BridgeClientDirectory.SaveClient("""
            {
              "BridgeBind": "100.64.237.3:8765",
              "OllamaUrl": "http://localhost:11434"
            }
            """, "100.64.237.3", 8765);
        Assert.NotNull(json);
        Assert.Contains("\"BridgeBind\": \"100.64.237.3:8765\"", json);
        Assert.Contains("http://localhost:11434", json);
        Assert.Contains("\"BeastHost\": \"100.64.237.3\"", json);
        Assert.Contains("\"BridgePort\": 8765", json);
        Assert.Null(BridgeClientDirectory.SaveClient("{ not json", "100.64.237.3", 8765));
    }

    [Fact]
    public void AndroidAndWindowsClientsDefaultToTailscaleAndDoNotListen()
    {
        var android = UniversalHost.Prepare(PlatformHostKind.Android, Layout(), _ => null);
        var windows = UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), _ => null);
        Assert.Equal("100.64.237.3", android.Remote!.Host);
        Assert.Equal("100.64.237.3", windows.Remote!.Host);
        Assert.False(android.BridgeEnabled);
        Assert.False(windows.BridgeEnabled);
        Assert.Null(android.BridgeBindHost);
        Assert.Null(windows.BridgeBindHost);

        var beast = UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), key => key == "ALICEVOICE_BRIDGE_BIND" ? "100.64.237.3:8765" : null);
        Assert.True(beast.BridgeEnabled);
        Assert.Null(beast.Remote);
        Assert.Null(beast.Options.OllamaUrl);
    }

    [Fact]
    public void OnlyTheBeastRegistersTheBridgeServer()
    {
        var client = new ServiceCollection().AddUniversal(UniversalHost.Prepare(PlatformHostKind.Android, Layout(), _ => null));
        var server = new ServiceCollection().AddUniversal(UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), key => key == "ALICEVOICE_BRIDGE_BIND" ? "100.64.237.3:8765" : null));
        Assert.Contains(client, d => d.ServiceType == typeof(BeastBridgeClient));
        Assert.DoesNotContain(client, d => d.ServiceType == typeof(BeastBridge));
        Assert.Contains(server, d => d.ServiceType == typeof(BeastBridge));
        Assert.DoesNotContain(server, d => d.ServiceType == typeof(BeastBridgeClient));
    }

    [Fact]
    public async Task TcpProbeAcceptsAnOpenPortAndRejectsAClosedOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var open = await BridgeClientDirectory.ProbeTcpAsync("127.0.0.1", port, CancellationToken.None);
        listener.Stop();
        Assert.True(open.Ok);
        var closed = await BridgeClientDirectory.ProbeTcpAsync("127.0.0.1", port, CancellationToken.None);
        Assert.False(closed.Ok);
    }

    [Fact]
    public async Task ASavedShortNameFallsThroughToTheTailscaleAddress()
    {
        var endpoint = new BridgeEndpoint("ahc-the-beast", 8765);
        var options = new AliceVoiceOptions { OllamaUrl = "http://ahc-the-beast:11434" };
        var settings = new BridgeClientSettings(endpoint, null, options);
        settings.Probe = (host, _, _) => Task.FromResult(new ProbeResult(host == "100.64.237.3", host == "100.64.237.3" ? "connected" : "connection refused"));
        await settings.EnsureReachableAsync(CancellationToken.None);
        Assert.Equal("100.64.237.3", endpoint.Host);
        Assert.Equal("http://100.64.237.3:11434", options.OllamaUrl);
        Assert.Equal("http://100.64.237.3:7851", options.AllTalkUrl);
    }

    private static HostLayout Layout() => new(null, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "voices.json"), Path.GetTempPath());
}
