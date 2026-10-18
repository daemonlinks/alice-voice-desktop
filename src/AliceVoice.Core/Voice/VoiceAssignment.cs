namespace AliceVoice.Core.Voice;

/// <summary>
/// A resolved voice: which provider to use and which voice within it.
/// </summary>
public sealed record VoiceProfile(VoiceProviderKind Provider, string Voice)
{
    public static VoiceProfile Default => new(VoiceProviderKind.Sapi, "");
}

/// <summary>
/// Assigns a voice provider + voice to a target. A target is either a persona
/// name (e.g. "Alice", "Plum") or a voice backend (e.g. AliceVoiceAgent,
/// LocalOllama). The long-term objective: every persona and every backend can
/// carry its own voice assignment.
/// </summary>
public sealed class VoiceAssignment
{
    private readonly Dictionary<string, VoiceProfile> _byPersona = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VoiceBackendKind, VoiceProfile> _byBackend = new();

    public VoiceProfile Default { get; set; } = VoiceProfile.Default;

    public void AssignPersona(string persona, VoiceProfile profile)
        => _byPersona[persona] = profile;

    public void AssignBackend(VoiceBackendKind backend, VoiceProfile profile)
        => _byBackend[backend] = profile;

    public VoiceProfile Resolve(string? persona, VoiceBackendKind? backend)
        {
            if (persona is not null && _byPersona.TryGetValue(persona, out var p)) return p;
            if (backend is { } b && _byBackend.TryGetValue(b, out var v)) return v;
            return Default;
        }

    public IReadOnlyDictionary<string, VoiceProfile> Personas => _byPersona;
    public IReadOnlyDictionary<VoiceBackendKind, VoiceProfile> Backends => _byBackend;
}
