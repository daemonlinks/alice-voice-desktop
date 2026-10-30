namespace AliceVoice.Core.Voice;

/// <summary>
/// A text event from a backend: "reply_partial" (streaming text so far),
/// "reply_text" (final reply, raised exactly once per turn), "backend_ready",
/// "reset_ack", "model_info", "tool_call", "error".
/// </summary>
public sealed record BackendEvent(string Type, string? Text);

/// <summary>Per-turn generation options (response profile + mood).</summary>
public sealed record AskOptions
{
    /// <summary>Maximum tokens to generate (null = backend default).</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Extra style guidance appended to the system prompt for this turn.</summary>
    public string? StyleHint { get; init; }

    public static AskOptions Default { get; } = new();
}

/// <summary>
/// Unified text backend (Alice LOCAL, local Ollama, the alice_voice agent).
/// Speech-to-text is NOT a backend concern: the app records an utterance,
/// transcribes it once with <see cref="Stt.ISttClient"/>, then calls
/// <see cref="AskAsync"/> with the text — the same path as typed chat.
/// Each backend keeps its own conversation history.
/// </summary>
public interface IVoiceBackend : IAsyncDisposable
{
    VoiceBackendKind Kind { get; }
    VoiceBackendSettings Settings { get; }

    event Action<BackendEvent>? EventReceived;

    /// <summary>Connect / verify reachability. Throws if the backend is not usable.</summary>
    Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default);

    /// <summary>Send one user turn (with conversation history) and return the final reply.</summary>
    Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default);

    /// <summary>Clear this backend's conversation history.</summary>
    void ResetConversation();

    /// <summary>Send a command (reset, switch_model).</summary>
    Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default);

    bool IsConnected { get; }
}
