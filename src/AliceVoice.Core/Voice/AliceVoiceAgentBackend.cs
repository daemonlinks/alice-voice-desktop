using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// The alice_voice Hermes agent backend: the Hermes API server's
/// OpenAI-compatible /p/alice_voice/v1 endpoint (default 127.0.0.1:8643),
/// with a rolling conversation history. Speech-to-text happens in the app
/// before <see cref="AskAsync"/> is called.
/// </summary>
public sealed class AliceVoiceAgentBackend : IVoiceBackend
{
    private static readonly FleetMember Member = FleetRoster.Find("alice_voice") ?? new FleetMember("alice_voice", "alice_voice", "Alice Voice");
    private HermesApiFleetChat _chat;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AliceVoiceAgentBackend(AliceVoiceOptions options)
    {
        Settings = VoiceBackendSettings.For(VoiceBackendKind.AliceVoiceAgent, options);
        _chat = new HermesApiFleetChat(Settings.AgentEndpoint);
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceVoiceAgent;
    public VoiceBackendSettings Settings { get; private set; }
    public event Action<BackendEvent>? EventReceived;
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        IsConnected = false;
        if (settings.AgentEndpoint != Settings.AgentEndpoint)
        {
            _chat.Dispose();
            _chat = new HermesApiFleetChat(settings.AgentEndpoint);
        }
        Settings = settings;
        await _chat.EnsureReachableAsync(Member, ct); // throws when unreachable / unauthorised
        IsConnected = true;
        Log.Information("alice_voice agent backend connected at {Url}", settings.AgentEndpoint);
        EventReceived?.Invoke(new BackendEvent("backend_ready", "alice_voice agent ready"));
    }

    public void ResetConversation() => _chat.ResetConversation(Member);

    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        switch (type)
        {
            case "reset":
                ResetConversation();
                EventReceived?.Invoke(new BackendEvent("reset_ack", "Agent conversation reset"));
                break;
            case "switch_model":
                EventReceived?.Invoke(new BackendEvent("model_info", $"Model: {Settings.Model} (set by the Hermes profile)"));
                break;
            default:
                Log.Information("Agent backend command {Type} (no-op)", type);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Send one turn to the alice_voice agent. Raises reply_text exactly once.</summary>
    public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var reply = await _chat.SendAsync(Member, prompt, ct);
            EventReceived?.Invoke(new BackendEvent("reply_text", reply));
            return reply;
        }
        finally { _lock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _chat.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
