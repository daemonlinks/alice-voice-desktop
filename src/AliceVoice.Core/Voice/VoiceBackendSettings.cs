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

    /// <summary>
    /// The one local brain the app runs — every Ollama-backed state uses it. It must be a
    /// tool-calling model: the Alice LOCAL backend sends native tool definitions and only
    /// degrades to inline JSON (unreliable) when the model does not advertise tool support.
    /// Measured 2026-11-12 against the app's own SOUL prompt and tool schema: 5/5 correct
    /// tool calls, ~0.9 s warm on the 1080 Ti. Do not introduce a second model here.
    /// </summary>
    public const string OllamaModel = "llama3.1:8b-instruct-q4_1";

    public string Model { get; init; } = OllamaModel;

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
        // Both Ollama-backed states (AliceLocal, LocalOllama) run the same brain: the record
        // default above. Only the Hermes agent state uses a different transport, and its
        // model is set by the Hermes profile.
        return kind == VoiceBackendKind.AliceVoiceAgent
            ? s with { Provider = "hermes", Model = "alice_voice" }
            : s;
    }
}
