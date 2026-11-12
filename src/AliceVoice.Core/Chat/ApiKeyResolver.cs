using System.Collections.Concurrent;
using AliceVoice.Core.Config;

namespace AliceVoice.Core.Chat;

/// <summary>
/// Resolves the API_SERVER_KEY a Hermes api_server expects. Each profile has its
/// OWN key, so a roster member asks for its profile's key; the shared key is the
/// last-resort fallback. The value is never logged.
/// </summary>
public static class ApiKeyResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>The shared key: process environment, then the hermes .env files.</summary>
    public static string Resolve() => Shared();

    /// <summary>The key for a roster member (prefer this form — see the three-argument one).</summary>
    public static string Resolve(FleetMember member) =>
        Resolve(member.Name, member.Profile, member.BaseUrl);

    /// <summary>
    /// The key for a roster member, looked up on the host that serves it:
    ///   1. env <c>API_SERVER_KEY_&lt;MEMBER&gt;</c> — explicit override, keyed by the MEMBER
    ///      name and NOT the profile: alice_windows, alice_5 and alice_arena all use the
    ///      profile `default` on three different hosts, each with its own key, so a
    ///      profile-keyed variable would hand one host's key to the others. This is also the
    ///      only route for a host whose key is not on this filesystem (the VPS).
    ///   2. That profile's own .env on the serving host:
    ///      Windows hermes → <c>&lt;hermes&gt;\profiles\&lt;profile&gt;\.env</c>, then <c>&lt;hermes&gt;\.env</c>
    ///      WSL host       → <c>\\wsl.localhost\Ubuntu\home\ahc\.hermes\...</c>
    ///      (the WSL home is readable from Windows — verified 2026-11-12)
    ///   3. The shared key.
    /// </summary>
    public static string Resolve(string name, string? profile, string? baseUrl)
    {
        var cacheKey = $"{name}|{profile}|{baseUrl}";
        if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
        var value = Lookup(name, profile, baseUrl);
        Cache[cacheKey] = value;
        return value;
    }

    /// <summary>Drop cached keys — for tests, and after a key rotation.</summary>
    public static void ClearCache() => Cache.Clear();

    private static string Lookup(string? name, string? profile, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var env = Environment.GetEnvironmentVariable($"API_SERVER_KEY_{name.ToUpperInvariant()}");
            if (!string.IsNullOrEmpty(env)) return env;

            // App-owned, so the app does not depend on machine-level environment
            // variables (item: a second user or machine had no route to the VPS keys).
            var app = FromAppKeyFile(name);
            if (!string.IsNullOrEmpty(app)) return app;
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            foreach (var path in ProfileEnvCandidates(profile, baseUrl))
            {
                var v = ReadKey(path);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return Shared();
    }

    private static IEnumerable<string> ProfileEnvCandidates(string profile, string? baseUrl)
    {
        if (IsWslHost(baseUrl))
        {
            // The WSL hermes home (Ubuntu) is readable from Windows over the WSL
            // filesystem share; that host's api_server reads its own .env files.
            const string root = @"\\wsl.localhost\Ubuntu\home\ahc\.hermes";
            yield return Path.Combine(root, "profiles", profile, ".env");
            yield return Path.Combine(root, ".env");
            yield break;
        }

        // The VPS api_server is a different machine: its .env is not on this
        // filesystem, so the env override above is the only route.
        if (IsVpsHost(baseUrl)) yield break;

        foreach (var hermes in new AliceVoiceOptions().HermesHomeCandidates)
        {
            yield return Path.Combine(hermes, "profiles", profile, ".env");
            yield return Path.Combine(hermes, ".env");
        }
    }

    private static bool IsWslHost(string? baseUrl) =>
        string.Equals(baseUrl?.TrimEnd('/'), FleetRoster.WslBaseUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IsVpsHost(string? baseUrl) =>
        string.Equals(baseUrl?.TrimEnd('/'), FleetRoster.VpsBaseUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The app-owned key file: <c>%LOCALAPPDATA%\AliceVoice\api-keys.env</c>, keys named
    /// <c>API_SERVER_KEY_&lt;MEMBER&gt;</c> exactly like the environment override, so an
    /// operator can hand the app the key for a host whose .env is not on this filesystem
    /// (the VPS) without setting a machine-level variable. <c>ALICEVOICE_KEYS_FILE</c>
    /// overrides the path, for tests. The value is never logged.
    /// </summary>
    private static string FromAppKeyFile(string name)
    {
        var want = $"API_SERVER_KEY_{name.ToUpperInvariant()}=";
        foreach (var path in AppKeyFileCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                    {
                        var v = line.Substring(want.Length).Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { /* ignore unreadable files */ }
        }
        return "";
    }

    private static IEnumerable<string> AppKeyFileCandidates()
    {
        var over = Environment.GetEnvironmentVariable("ALICEVOICE_KEYS_FILE");
        if (!string.IsNullOrEmpty(over))
        {
            yield return over;
            yield break;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "AliceVoice", "api-keys.env");
    }

    private static string Shared()
    {
        var env = Environment.GetEnvironmentVariable("API_SERVER_KEY");
        if (!string.IsNullOrEmpty(env)) return env;

        foreach (var hermes in new AliceVoiceOptions().HermesHomeCandidates)
        {
            var candidates = new[]
            {
                Path.Combine(hermes, ".env"),
                Path.Combine(hermes, "profiles", "alice_voice", ".env"),
                Path.Combine(hermes, "profiles", "default", ".env")
            };

            foreach (var path in candidates)
            {
                var v = ReadKey(path);
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }

        return "";
    }

    /// <summary>
    /// Reads API_SERVER_KEY from a .env file. The value is returned to the caller
    /// only — it is never logged.
    /// </summary>
    private static string ReadKey(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("API_SERVER_KEY=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = line.Substring("API_SERVER_KEY=".Length).Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
        }
        catch { /* ignore unreadable files */ }
        return "";
    }
}
