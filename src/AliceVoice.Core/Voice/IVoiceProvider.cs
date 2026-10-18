namespace AliceVoice.Core.Voice;

/// <summary>
/// The TTS engines the app can speak through. Each is a switchable voice
/// provider; a voice assignment maps a target (persona name or backend) to a
/// provider + voice.
/// </summary>
public enum VoiceProviderKind
{
    /// <summary>AllTalk TTS server (direct HTTP, no relay). High-quality voices.</summary>
    AllTalk,

    /// <summary>Windows SAPI (System.Speech). Instant, local, no network.</summary>
    Sapi,

    /// <summary>Piper TTS (fully local, fleet-standard) via CLI. Runs piper.exe.</summary>
    Piper
}

/// <summary>
/// A concrete TTS engine. Implementations live in the Audio project
/// (AllTalkTtsProvider, SapiTtsProvider).
/// </summary>
public interface IVoiceProvider
{
    VoiceProviderKind Kind { get; }
    string DisplayName { get; }

    /// <summary>List the voices this provider can speak with.</summary>
    Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default);

    /// <summary>Speak text with the given voice (provider-specific name).</summary>
    Task SpeakAsync(string text, string voice, CancellationToken ct = default);
}
