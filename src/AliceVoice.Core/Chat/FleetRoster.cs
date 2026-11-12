namespace AliceVoice.Core.Chat;

/// <summary>
/// A fleet member the chat tool can talk to. Each maps to a Hermes profile
/// served by the API server at /p/<profile>/v1.
/// </summary>
public sealed record FleetMember(
    string Name,        // canonical agent name, e.g. alice_voice
    string Profile,     // Hermes profile path segment, e.g. alice_voice
    string DisplayName, // human label
    string? ApiKey = null,  // per-profile API_SERVER_KEY; null = use shared key
    string? BaseUrl = null); // null = the session's Hermes api_server (this host)

/// <summary>
/// The fleet roster — every member the chat tool can reach. Each host runs its
/// own Hermes api_server that multiplexes ITS profiles at /p/&lt;profile&gt;/v1, so a
/// member carries its own BaseUrl when it lives off this host; null means the
/// session's api_server. Each profile needs its own API_SERVER_KEY (only
/// profiles with a key are reachable).
///
/// Measured 2026-11-12 — do not "tidy" these without re-probing:
///   alice_5 and alice_arena are their host's `default` profile, NOT profiles
///   named after them (/p/alice_5/... and /p/alice_arena/... return 404).
///   Windows reaches the WSL hermes through the WSL2 localhost relay, so
///   127.0.0.1:8642 on the Beast IS the WSL api_server. The VPS api_server
///   listens on 127.0.0.1:8642 inside the VPS, so the Tailscale address below
///   only works once the owner binds/proxies it.
/// </summary>
public static class FleetRoster
{
    /// <summary>The WSL host's hermes api_server (via the WSL2 localhost relay).</summary>
    public const string WslBaseUrl = "http://127.0.0.1:8642";

    /// <summary>The VPS hermes api_server over Tailscale (loopback-bound today).</summary>
    public const string VpsBaseUrl = "http://100.76.25.98:8642";

    public static readonly IReadOnlyList<FleetMember> Members = new[]
    {
        // Windows host — the session's own api_server (base URL from settings).
        new FleetMember("alice_windows", "default", "alice_windows (Windows)"),
        new FleetMember("alice_voice", "alice_voice", "alice_voice (Windows)"),
        new FleetMember("alice_2", "alice_2", "alice_2 (Windows)"),
        new FleetMember("alice_3", "alice_3", "alice_3 (Windows)"),
        new FleetMember("alice_4", "alice_4", "alice_4 (Windows)"),
        // WSL host — alice_5 is that host's `default` profile.
        new FleetMember("alice_5", "default", "alice_5 (WSL)", BaseUrl: WslBaseUrl),
        new FleetMember("alice_1", "alice_1", "alice_1 (WSL)", BaseUrl: WslBaseUrl),
        new FleetMember("alice_7", "alice_7", "alice_7 (WSL)", BaseUrl: WslBaseUrl),
        // VPS host — alice_arena is that host's `default` profile.
        new FleetMember("alice_arena", "default", "alice_arena (VPS)", BaseUrl: VpsBaseUrl),
        new FleetMember("alice_6", "alice_6", "alice_6 (VPS)", BaseUrl: VpsBaseUrl)
    };

    public static FleetMember? Find(string name) =>
        Members.FirstOrDefault(m => m.Name == name);
}
