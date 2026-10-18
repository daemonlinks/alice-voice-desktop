using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Routes speech to the right provider + voice for a target (persona or
/// backend). Holds all registered providers warm; switching is O(1).
/// </summary>
public interface IVoiceProviderManager
{
    IReadOnlyList<IVoiceProvider> Providers { get; }
    VoiceAssignment Assignment { get; }

    /// <summary>Speak text for a persona (e.g. "Alice", "Plum").</summary>
    Task SpeakForPersonaAsync(string persona, string text, CancellationToken ct = default);

    /// <summary>Speak text for a voice backend.</summary>
    Task SpeakForBackendAsync(VoiceBackendKind backend, string text, CancellationToken ct = default);

    /// <summary>Speak text with an explicit provider + voice.</summary>
    Task SpeakAsync(VoiceProfile profile, string text, CancellationToken ct = default);
}

public sealed class VoiceProviderManager : IVoiceProviderManager
{
    private readonly Dictionary<VoiceProviderKind, IVoiceProvider> _providers = new();

    public VoiceProviderManager(IEnumerable<IVoiceProvider> providers)
    {
        foreach (var p in providers)
            _providers[p.Kind] = p;
        Assignment = new VoiceAssignment
        {
            // Sensible defaults: Alice/Plum on AllTalk (high quality), backends on SAPI (instant).
            Default = new VoiceProfile(VoiceProviderKind.Sapi, "")
        };
        Assignment.AssignPersona("alice", new VoiceProfile(VoiceProviderKind.AllTalk, "female_07.wav"));
        Assignment.AssignPersona("plum", new VoiceProfile(VoiceProviderKind.AllTalk, "female_06.wav"));
        Assignment.AssignBackend(VoiceBackendKind.AliceVoiceAgent, new VoiceProfile(VoiceProviderKind.Sapi, ""));
        Assignment.AssignBackend(VoiceBackendKind.LocalOllama, new VoiceProfile(VoiceProviderKind.Sapi, ""));
    }

    public IReadOnlyList<IVoiceProvider> Providers => _providers.Values.ToList();
    public VoiceAssignment Assignment { get; }

    public Task SpeakForPersonaAsync(string persona, string text, CancellationToken ct = default)
        => SpeakAsync(Assignment.Resolve(persona, null), text, ct);

    public Task SpeakForBackendAsync(VoiceBackendKind backend, string text, CancellationToken ct = default)
        => SpeakAsync(Assignment.Resolve(null, backend), text, ct);

    public async Task SpeakAsync(VoiceProfile profile, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!_providers.TryGetValue(profile.Provider, out var provider))
        {
            Log.Warning("No voice provider registered for {Kind}", profile.Provider);
            return;
        }
        Log.Information("Speaking via {Provider} (voice: {Voice}): {Text}",
            provider.DisplayName, string.IsNullOrEmpty(profile.Voice) ? "default" : profile.Voice, text);
        await provider.SpeakAsync(text, profile.Voice, ct);
    }
}
