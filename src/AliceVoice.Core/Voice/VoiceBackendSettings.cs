namespace AliceVoice.Core.Voice;

/// <summary>
/// The three operating states of the app. Each routes the voice pipeline
/// (STT → LLM → TTS) through a different backend.
/// </summary>
public enum VoiceBackendKind
{
    /// <summary>Existing Alice Voice web relay (ws://localhost:9876).</summary>
    Relay,

    /// <summary>The alice_voice Hermes agent (voice + provider/model settings).</summary>
    AliceVoiceAgent,

    /// <summary>Local Ollama backend with a defined model + voice (STT/TTS configurable).</summary>
    LocalOllama
}

/// <summary>
/// Per-backend configuration: which LLM provider/model and which STT/TTS
/// engines to use. Each state carries its own settings.
/// </summary>
public sealed record VoiceBackendSettings
{
    public VoiceBackendKind Kind { get; init; }

    // LLM
    public string Provider { get; init; } = "ollama";   // ollama | openrouter | hermes
    public string Model { get; init; } = "dolphin-llama3:8b";

    // STT
    public string SttEngine { get; init; } = "whisper"; // whisper | faster-whisper
    public string SttModel { get; init; } = "base";

    // TTS
    public string TtsEngine { get; init; } = "edge";    // alltalk | edge
    public string TtsVoice { get; init; } = "en-GB-SoniaNeural";

    // Endpoints
    public string RelayUrl { get; init; } = "ws://localhost:9876";
    public string OllamaUrl { get; init; } = "http://localhost:11434";
    public string AgentEndpoint { get; init; } = "http://localhost:9876";

    public static VoiceBackendSettings For(VoiceBackendKind kind) => kind switch
    {
        VoiceBackendKind.Relay => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "openrouter",
            Model = "cydonia-24b-v4.1",
            SttEngine = "whisper",
            TtsEngine = "alltalk"
        },
        VoiceBackendKind.AliceVoiceAgent => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "hermes",
            Model = "alice_voice",
            SttEngine = "whisper",
            TtsEngine = "edge"
        },
        VoiceBackendKind.LocalOllama => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "ollama",
            Model = "dolphin-llama3:8b",
            SttEngine = "whisper",
            SttModel = "base",
            TtsEngine = "edge",
            TtsVoice = "en-GB-SoniaNeural"
        },
        _ => new VoiceBackendSettings { Kind = kind }
    };
}
