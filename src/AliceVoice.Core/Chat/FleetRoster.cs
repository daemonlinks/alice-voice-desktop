namespace AliceVoice.Core.Chat;

/// <summary>
/// A fleet member the chat tool can talk to. Each maps to a Hermes profile
/// served by the API server at /p/<profile>/v1.
/// </summary>
public sealed record FleetMember(
    string Name,        // canonical agent name, e.g. alice_voice
    string Profile,     // Hermes profile path segment, e.g. alice_voice
    string DisplayName, // human label
    string? ApiKey = null); // per-profile API_SERVER_KEY; null = use shared key

/// <summary>
/// The fleet roster — every member the chat tool can reach. The API server
/// multiplexes profiles at /p/<profile>/v1; each needs its own API_SERVER_KEY
/// (only profiles with a key set are reachable).
/// </summary>
public static class FleetRoster
{
    public static readonly IReadOnlyList<FleetMember> Members = new[]
    {
        new FleetMember("alice_voice", "alice_voice", "Alice Voice"),
        new FleetMember("alice_2", "alice_2", "Alice 2"),
        new FleetMember("alice_3", "alice_3", "Alice 3"),
        new FleetMember("alice_4", "alice_4", "Alice 4"),
        new FleetMember("alice_windows", "default", "Alice (Windows)"),
        new FleetMember("alice_5", "alice_5", "Alice 5 (WSL)"),
        new FleetMember("alice_1", "alice_1", "Alice 1 (WSL)"),
        new FleetMember("alice_7", "alice_7", "Alice 7 (WSL)"),
        new FleetMember("alice_arena", "alice_arena", "Alice Arena (VPS)"),
        new FleetMember("alice_6", "alice_6", "Alice 6 (VPS)")
    };

    public static FleetMember? Find(string name) =>
        Members.FirstOrDefault(m => m.Name == name);
}
