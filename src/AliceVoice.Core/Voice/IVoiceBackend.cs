namespace AliceVoice.Core.Voice;

/// <summary>
/// Raised when the backend produces a text event (transcript, reply, lifecycle).
/// </summary>
public sealed record BackendEvent(string Type, string? Text);

/// <summary>
/// Unified voice backend. Each of the three states implements this: the relay,
/// the alice_voice agent, and local Ollama. The app talks to one backend at a
/// time and can switch between them at runtime.
/// </summary>
public interface IVoiceBackend : IAsyncDisposable
{
    VoiceBackendKind Kind { get; }
    VoiceBackendSettings Settings { get; }

    event Action<BackendEvent>? EventReceived;
    event Action<byte[]>? AudioReceived;

    /// <summary>Connect to the backend with the given settings.</summary>
    Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default);

    /// <summary>Send a 16 kHz mono Int16 PCM frame for STT.</summary>
    Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default);

    /// <summary>Send a text command (reset, switch model, set voice, etc).</summary>
    Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default);

    bool IsConnected { get; }
}
