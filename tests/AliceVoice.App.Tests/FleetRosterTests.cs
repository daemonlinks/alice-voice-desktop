using AliceVoice.Core.Chat;
using Xunit;

namespace AliceVoice.App.Tests;

/// <summary>
/// Pins the roster facts measured on the live fleet 2026-11-12. These are the
/// mapping errors that made WSL/VPS agents unreachable from App U: every member
/// was pointed at one api_server, and the two host-default agents were looked up
/// under their own name (/{profile} 404) instead of `default`.
/// </summary>
public class FleetRosterTests
{
    private static FleetMember Member(string name) =>
        FleetRoster.Find(name) ?? throw new Xunit.Sdk.XunitException($"roster member {name} missing");

    [Fact]
    public void EveryFleetAgentIsOnTheRoster()
    {
        var names = FleetRoster.Members.Select(m => m.Name).ToHashSet();
        foreach (var expected in new[]
                 {
                     "alice_windows", "alice_voice", "alice_2", "alice_3", "alice_4",
                     "alice_5", "alice_1", "alice_7", "alice_arena", "alice_6"
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void DisplayNamesAreTheRealAgentNames()
    {
        foreach (var m in FleetRoster.Members)
            Assert.StartsWith(m.Name, m.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void HostDefaultAgentsUseTheDefaultProfile()
    {
        // alice_5 is WSL's `default`; alice_arena is the VPS's `default`.
        Assert.Equal("default", Member("alice_5").Profile);
        Assert.Equal("default", Member("alice_arena").Profile);
        Assert.Equal("default", Member("alice_windows").Profile);
    }

    [Fact]
    public void OffHostAgentsCarryTheirOwnBaseUrl()
    {
        foreach (var name in new[] { "alice_5", "alice_1", "alice_7" })
            Assert.Equal(FleetRoster.WslBaseUrl, Member(name).BaseUrl);

        foreach (var name in new[] { "alice_arena", "alice_6" })
            Assert.Equal(FleetRoster.VpsBaseUrl, Member(name).BaseUrl);

        // Windows members use the session's configured api_server.
        foreach (var name in new[] { "alice_windows", "alice_voice", "alice_2", "alice_3", "alice_4" })
            Assert.Null(Member(name).BaseUrl);
    }

    [Fact]
    public void AProfileKeyIsFoundForEachLocalProfile()
    {
        // alice_voice's key lives in her own profile .env on this host.
        Assert.NotEmpty(ApiKeyResolver.Resolve("alice_voice", "alice_voice", null));
        Assert.NotEmpty(ApiKeyResolver.Resolve("alice_windows", "default", null));
    }

    [Fact]
    public void WslProfileKeysAreReadableFromWindows()
    {
        // The WSL hermes home is read over the WSL filesystem share. If the share
        // is not present on this machine that is not a failure — but if it IS
        // present, the key must resolve.
        const string shareRoot = @"\\wsl.localhost\Ubuntu\home\ahc\.hermes";
        if (!Directory.Exists(shareRoot)) return;

        Assert.True(File.Exists($@"{shareRoot}\profiles\alice_1\.env"),
            "the WSL share is visible, so alice_1's .env should exist");
        Assert.NotEmpty(ApiKeyResolver.Resolve("alice_1", "alice_1", FleetRoster.WslBaseUrl));
    }

    [Fact]
    public void TheEnvOverrideIsKeyedByMemberNotProfile()
    {
        // alice_windows, alice_5 and alice_arena all use the profile `default`, on three
        // different hosts with three different keys. A profile-keyed override would hand
        // one host's key to the others, so the override is keyed by MEMBER name.
        const string probe = "probe-not-a-real-key";
        Environment.SetEnvironmentVariable("API_SERVER_KEY_ALICE_ARENA", probe);
        ApiKeyResolver.ClearCache();
        try
        {
            Assert.Equal(probe, ApiKeyResolver.Resolve("alice_arena", "default", FleetRoster.VpsBaseUrl));
            Assert.NotEqual(probe, ApiKeyResolver.Resolve("alice_windows", "default", null));
            Assert.NotEqual(probe, ApiKeyResolver.Resolve("alice_5", "default", FleetRoster.WslBaseUrl));
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_SERVER_KEY_ALICE_ARENA", null);
            Environment.SetEnvironmentVariable("API_SERVER_KEY_DEFAULT", null);
            ApiKeyResolver.ClearCache();
        }
    }

    [Fact]
    public void AProfileKeyedOverrideWouldNotBeUsed()
    {
        // The collision this guards against: API_SERVER_KEY_DEFAULT must NOT be read,
        // because `default` is a profile name on three hosts at once.
        const string probe = "probe-profile-keyed";
        Environment.SetEnvironmentVariable("API_SERVER_KEY_DEFAULT", probe);
        ApiKeyResolver.ClearCache();
        try
        {
            Assert.NotEqual(probe, ApiKeyResolver.Resolve("alice_windows", "default", null));
            Assert.NotEqual(probe, ApiKeyResolver.Resolve("alice_arena", "default", FleetRoster.VpsBaseUrl));
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_SERVER_KEY_DEFAULT", null);
            ApiKeyResolver.ClearCache();
        }
    }
}
