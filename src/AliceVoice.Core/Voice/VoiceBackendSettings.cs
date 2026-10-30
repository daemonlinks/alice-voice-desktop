using AliceVoice.Core.Config;

namespace AliceVoice.Core.Voice;

/// <summary>
/// The operating states of the app. Each routes the LLM turn through a
/// different backend (speech-to-text and speech output are shared).
/// </summary>
public enum VoiceBackendKind
{
    /// <summary>The alice_voice Hermes agent via the Hermes API server.</summary>
    AliceVoiceAgent,

    /// <summary>Plain local Ollama chat (no SOUL, no tools).</summary>
    LocalOllama,

    /// <summary>Alice LOCAL — Ollama + the Alice LOCAL SOUL as system prompt + tools.</summary>
    AliceLocal
}

/// <summary>
/// Per-backend LLM configuration. Endpoints default from
/// <see cref="AliceVoiceOptions"/> (appsettings.json / ALICEVOICE_* env), never
/// from hardcoded user paths. Speech output is chosen per agent in the UI
/// (voice-assignments.json), not here.
/// </summary>
public sealed record VoiceBackendSettings
{
    public VoiceBackendKind Kind { get; init; }

    /// <summary>ollama | hermes</summary>
    public string Provider { get; init; } = "ollama";
    public string Model { get; init; } = "dolphin-llama3:8b";

    public string OllamaUrl { get; init; } = "http://localhost:11434";

    /// <summary>Hermes API server (OpenAI-compatible, /p/&lt;profile&gt;/v1). 8643, not 8642 (wslrelay).</summary>
    public string AgentEndpoint { get; init; } = "http://127.0.0.1:8643";

    public static VoiceBackendSettings For(VoiceBackendKind kind, AliceVoiceOptions? options = null)
    {
        var o = options ?? new AliceVoiceOptions();
        var s = new VoiceBackendSettings
        {
            Kind = kind,
            OllamaUrl = o.ResolvedOllamaUrl,
            AgentEndpoint = o.ResolvedHermesApiUrl
        };
        return kind == VoiceBackendKind.AliceVoiceAgent
            ? s with { Provider = "hermes", Model = "alice_voice" }
            : s;
    }
}
