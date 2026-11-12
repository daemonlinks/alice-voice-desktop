using System;
using System.IO;
using System.Linq;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using Xunit;

namespace AliceVoice.App.Tests;

/// <summary>
/// The key resolver must never follow a profile-scoped HERMES_HOME.
///
/// A Hermes agent session exports HERMES_HOME=&lt;hermes&gt;\profiles\&lt;agent&gt;, and App U
/// launched from such a shell resolved six of ten members' keys out of the wrong
/// home — every one of them a 401 (measured 2026-11-12, through the app's own
/// /v1/fleet/send). Only a folder that actually holds `profiles` counts as a hermes
/// home; the standard install home is always tried as well.
/// </summary>
public class ApiKeyResolverTests
{
    private static string TempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appu-keytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WithHermesHome(string value, Action body)
    {
        var saved = Environment.GetEnvironmentVariable("HERMES_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", value);
            ApiKeyResolver.ClearCache();
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", saved);
            ApiKeyResolver.ClearCache();
        }
    }

    [Fact]
    public void AProfileScopedHermesHomeIsNotTreatedAsAHome()
    {
        var decoyRoot = TempHome();
        var decoyProfile = Path.Combine(decoyRoot, "profiles", "alice_4");
        Directory.CreateDirectory(decoyProfile);
        // The decoy carries a valid-looking .env, exactly like a real profile folder.
        File.WriteAllText(Path.Combine(decoyProfile, ".env"), "API_SERVER_KEY=decoykey\n");

        try
        {
            WithHermesHome(decoyProfile, () =>
            {
                var homes = new AliceVoiceOptions().HermesHomeCandidates;

                Assert.DoesNotContain(homes,
                    h => string.Equals(Path.GetFullPath(h), Path.GetFullPath(decoyProfile), StringComparison.OrdinalIgnoreCase));
                Assert.Contains(homes, h => Directory.Exists(Path.Combine(h, "profiles")));

                // A Windows member must not resolve its key out of the decoy home.
                Assert.NotEqual("decoykey", ApiKeyResolver.Resolve("alice_4", "alice_4", null));
                // …and the shared key must not come from it either.
                Assert.NotEqual("decoykey", ApiKeyResolver.Resolve());
            });
        }
        finally
        {
            try { Directory.Delete(decoyRoot, true); } catch { }
        }
    }

    [Fact]
    public void ARealHermesHomeIsStillUsed()
    {
        var home = TempHome();
        var profile = Path.Combine(home, "profiles", "alice_2");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(home, "profiles", "alice_2", ".env"), "API_SERVER_KEY=fromtempkey\n");
        File.WriteAllText(Path.Combine(home, ".env"), "API_SERVER_KEY=sharetempkey\n");

        try
        {
            WithHermesHome(home, () =>
            {
                Assert.Equal(Path.GetFullPath(home), new AliceVoiceOptions().HermesHome);
                Assert.Equal("fromtempkey", ApiKeyResolver.Resolve("alice_2", "alice_2", null));
                Assert.Equal("sharetempkey", ApiKeyResolver.Resolve());
            });
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void MemberKeyedEnvOverrideBeatsEveryFile()
    {
        var home = TempHome();
        Directory.CreateDirectory(Path.Combine(home, "profiles", "alice_6"));
        File.WriteAllText(Path.Combine(home, "profiles", "alice_6", ".env"), "API_SERVER_KEY=filekey\n");

        var varName = "API_SERVER_KEY_ALICE_6";
        var saved = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, "envkey");
            WithHermesHome(home, () =>
            {
                // A host whose key is not on this filesystem (the VPS) is reachable only
                // through the member-keyed override — and the override is keyed by MEMBER,
                // never by profile: `default` exists on three hosts with three keys.
                Assert.Equal("envkey", ApiKeyResolver.Resolve("alice_6", "alice_6", FleetRoster.VpsBaseUrl));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, saved);
            try { Directory.Delete(home, true); } catch { }
        }
    }

    [Fact]
    public void AppOwnedKeyFileServesAHostWhoseEnvIsNotOnThisFilesystem()
    {
        // A second user or machine has no HKCU override and no VPS .env; the app-owned key
        // file (%LOCALAPPDATA%\AliceVoice\api-keys.env, overridden here for the test) is the
        // durable route to alice_arena / alice_6 keys.
        var keys = Path.Combine(Path.GetTempPath(), "alicevoice-keys-" + Guid.NewGuid().ToString("N") + ".env");
        File.WriteAllText(keys, "API_SERVER_KEY_ALICE_ARENA=appfilekey\n");

        var fileVar = "ALICEVOICE_KEYS_FILE";
        var memberVar = "API_SERVER_KEY_ALICE_ARENA";
        var savedFile = Environment.GetEnvironmentVariable(fileVar);
        var savedMember = Environment.GetEnvironmentVariable(memberVar);
        try
        {
            Environment.SetEnvironmentVariable(memberVar, null); // the machine override would win over the file
            Environment.SetEnvironmentVariable(fileVar, keys);
            ApiKeyResolver.ClearCache();

            Assert.Equal("appfilekey", ApiKeyResolver.Resolve("alice_arena", "default", FleetRoster.VpsBaseUrl));
        }
        finally
        {
            Environment.SetEnvironmentVariable(memberVar, savedMember);
            Environment.SetEnvironmentVariable(fileVar, savedFile);
            ApiKeyResolver.ClearCache();
            try { File.Delete(keys); } catch { }
        }
    }
}
