namespace AliceVoice.Core.Voice;

/// <summary>
/// The operating states of the app. Each routes the voice pipeline
/// (STT → LLM → TTS) through a different backend.
/// </summary>
public enum VoiceBackendKind
{
    /// <summary>The alice_voice Hermes agent (voice + provider/model settings).</summary>
    AliceVoiceAgent,

    /// <summary>Local Ollama backend with a defined model + voice (STT/TTS configurable).</summary>
    LocalOllama,

    /// <summary>Alice LOCAL — fully local: Ollama LLM + the local SOUL file as system prompt.</summary>
    AliceLocal
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
    public string SttModel { get; init; } = "large-v3";

    // TTS
    public string TtsEngine { get; init; } = "edge";    // alltalk | edge
    public string TtsVoice { get; init; } = "en-GB-SoniaNeural";

    // Endpoints
    public string OllamaUrl { get; init; } = "http://localhost:11434";
    public string AgentEndpoint { get; init; } = "http://127.0.0.1:8642";

    // Local SOUL file used as the system prompt for the Alice LOCAL backend.
    public string SoulPath { get; init; } = @"C:\Users\AHC.DAEMONLINKS\AppData\Local\hermes\profiles\alice_voice\SOUL.md";

    public static VoiceBackendSettings For(VoiceBackendKind kind) => kind switch
    {
        VoiceBackendKind.AliceVoiceAgent => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "hermes",
            Model = "alice_voice",
            SttEngine = "whisper",
            SttModel = "large-v3",
            TtsEngine = "edge"
        },
        VoiceBackendKind.LocalOllama => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "ollama",
            Model = "dolphin-llama3:8b",
            SttEngine = "whisper",
            SttModel = "large-v3",
            TtsEngine = "edge",
            TtsVoice = "en-GB-SoniaNeural"
        },
        VoiceBackendKind.AliceLocal => new VoiceBackendSettings
        {
            Kind = kind,
            Provider = "ollama",
            Model = "dolphin-llama3:8b",
            SttEngine = "whisper",
            SttModel = "large-v3",
            TtsEngine = "edge",
            TtsVoice = "en-GB-SoniaNeural"
        },
        _ => new VoiceBackendSettings { Kind = kind }
    };
}
