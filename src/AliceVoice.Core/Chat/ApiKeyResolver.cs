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

    /// <summary>
    /// The key for a roster member's profile, looked up on the host that serves it:
    ///   1. env <c>API_SERVER_KEY_&lt;PROFILE&gt;</c> — explicit override, and the only
    ///      route for a host whose key is not on this filesystem (the VPS).
    ///   2. That profile's own .env on the serving host:
    ///      Windows hermes → <c>&lt;hermes&gt;\profiles\&lt;profile&gt;\.env</c>, then <c>&lt;hermes&gt;\.env</c>
    ///      WSL host       → <c>\\wsl.localhost\Ubuntu\home\ahc\.hermes\...</c>
    ///      (the WSL home is readable from Windows — verified 2026-11-12)
    ///   3. The shared key.
    /// </summary>
    public static string Resolve(string? profile, string? baseUrl)
    {
        var cacheKey = $"{profile}|{baseUrl}";
        if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
        var value = Lookup(profile, baseUrl);
        Cache[cacheKey] = value;
        return value;
    }

    private static string Lookup(string? profile, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var env = Environment.GetEnvironmentVariable($"API_SERVER_KEY_{profile.ToUpperInvariant()}");
            if (!string.IsNullOrEmpty(env)) return env;

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

        var hermes = new AliceVoiceOptions().HermesHome;
        yield return Path.Combine(hermes, "profiles", profile, ".env");
        yield return Path.Combine(hermes, ".env");
    }

    private static bool IsWslHost(string? baseUrl) =>
        string.Equals(baseUrl?.TrimEnd('/'), FleetRoster.WslBaseUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IsVpsHost(string? baseUrl) =>
        string.Equals(baseUrl?.TrimEnd('/'), FleetRoster.VpsBaseUrl, StringComparison.OrdinalIgnoreCase);

    private static string Shared()
    {
        var env = Environment.GetEnvironmentVariable("API_SERVER_KEY");
        if (!string.IsNullOrEmpty(env)) return env;

        var hermes = new AliceVoiceOptions().HermesHome;
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
