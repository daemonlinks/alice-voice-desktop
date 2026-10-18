using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Owns the current voice backend and switches between the three states
/// (Relay / alice_voice agent / local Ollama) at runtime. The app talks to
/// this manager, never to a backend directly.
/// </summary>
public interface IVoiceBackendManager : IAsyncDisposable
{
    VoiceBackendKind CurrentKind { get; }
    IVoiceBackend Current { get; }

    event Action<BackendEvent>? EventReceived;
    event Action<byte[]>? AudioReceived;

    /// <summary>Switch to the given backend kind with its settings.</summary>
    Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default);

    /// <summary>Send a 16 kHz mono Int16 PCM frame to the current backend.</summary>
    Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default);

    /// <summary>Send a text command to the current backend.</summary>
    Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default);
}

public sealed class VoiceBackendManager : IVoiceBackendManager
{
    private readonly Dictionary<VoiceBackendKind, IVoiceBackend> _backends;
    private IVoiceBackend _current;

    public VoiceBackendManager(ILocalTts tts)
    {
        _backends = new Dictionary<VoiceBackendKind, IVoiceBackend>
        {
            [VoiceBackendKind.Relay] = new RelayBackend(),
            [VoiceBackendKind.AliceVoiceAgent] = new AliceVoiceAgentBackend(),
            [VoiceBackendKind.LocalOllama] = new LocalOllamaBackend(tts)
        };
        _current = _backends[VoiceBackendKind.Relay];

        foreach (var b in _backends.Values)
        {
            b.EventReceived += e => EventReceived?.Invoke(e);
            b.AudioReceived += a => AudioReceived?.Invoke(a);
        }
    }

    public VoiceBackendKind CurrentKind => _current.Kind;
    public IVoiceBackend Current => _current;

    public event Action<BackendEvent>? EventReceived;
    public event Action<byte[]>? AudioReceived;

    public async Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default)
    {
        if (_current.Kind == kind && _current.IsConnected)
        {
            Log.Information("Already on backend {Kind}", kind);
            return;
        }

        // Tear down the current backend.
        await _current.DisposeAsync();

        var next = _backends[kind];
        await next.ConnectAsync(settings ?? VoiceBackendSettings.For(kind), ct);
        _current = next;

        Log.Information("Switched voice backend to {Kind}", kind);
        EventReceived?.Invoke(new BackendEvent("backend_switched", $"Backend: {kind}"));
    }

    public Task SendAudioAsync(short[] pcm16k, CancellationToken ct = default)
        => _current.SendAudioAsync(pcm16k, ct);

    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
        => _current.SendCommandAsync(type, payload, ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _backends.Values)
            await b.DisposeAsync();
    }
}
