using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// A fleet member turn is a FULL agent turn, so the client that waits for it must outlast one.
/// Measured 2026-11-22: a 120 s HttpClient timeout abandoned a turn the brain finished at 2 m 20 s,
/// and the app reported "The request was canceled due to the configured HttpClient.Timeout of 120
/// seconds elapsing." These tests pin the ceiling so it cannot quietly shrink again.
/// </summary>
public class FleetTurnTimeoutTests
{
    [Fact]
    public void ClientDefaultOutlastsAFullAgentTurn()
    {
        Assert.True(HermesApiFleetChat.DefaultTurnTimeout >= TimeSpan.FromMinutes(4),
            $"fleet turn ceiling {HermesApiFleetChat.DefaultTurnTimeout} is too short for a full agent turn");
    }

    [Fact]
    public void ClientUsesItsDefaultUnlessToldOtherwise()
    {
        using var chat = new HermesApiFleetChat("http://127.0.0.1:8643");
        Assert.Equal(HermesApiFleetChat.DefaultTurnTimeout, chat.TurnTimeout);
    }

    [Fact]
    public void AnExplicitTimeoutIsHonoured()
    {
        using var chat = new HermesApiFleetChat("http://127.0.0.1:8643", turnTimeout: TimeSpan.FromSeconds(240));
        Assert.Equal(TimeSpan.FromSeconds(240), chat.TurnTimeout);
    }

    [Fact]
    public void OptionsDefaultToTheClientCeiling()
    {
        Assert.Equal(HermesApiFleetChat.DefaultTurnTimeout, new AliceVoiceOptions().ResolvedFleetTurnTimeout);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(0, 30)]
    [InlineData(600, 600)]
    [InlineData(99999, 3600)]
    public void ConfiguredSecondsAreClamped(int configured, int expected)
    {
        var options = new AliceVoiceOptions { FleetTurnTimeoutSeconds = configured };
        Assert.Equal(TimeSpan.FromSeconds(expected), options.ResolvedFleetTurnTimeout);
    }

    [Fact]
    public void TheEnvironmentOverrideWins()
    {
        var missing = Path.Combine(Path.GetTempPath(), "alicevoice-no-such-settings.json");
        var options = AliceVoiceOptions.Load(missing, name => name == "ALICEVOICE_FLEET_TIMEOUT" ? "480" : null);
        Assert.Equal(TimeSpan.FromSeconds(480), options.ResolvedFleetTurnTimeout);
    }
}
