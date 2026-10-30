using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using Serilog;

namespace AliceVoice.Core.Voice;

/// <summary>
/// Owns the text backends (alice_voice agent / local Ollama / Alice LOCAL),
/// switches between them at runtime and owns the single warm STT engine.
/// The app talks to this manager, never to a backend directly.
/// </summary>
public interface IVoiceBackendManager : IAsyncDisposable
{
    VoiceBackendKind CurrentKind { get; }
    IVoiceBackend Current { get; }

    /// <summary>The backend instance for a kind (for kind-specific features such as Remember).</summary>
    IVoiceBackend Get(VoiceBackendKind kind);

    event Action<BackendEvent>? EventReceived;

    /// <summary>Switch to (and if needed connect) the given backend. Throws if it can't connect; the current backend is unchanged then.</summary>
    Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default);

    /// <summary>Force a fresh connection to the current backend.</summary>
    Task ReconnectAsync(CancellationToken ct = default);

    /// <summary>Transcribe one complete utterance (16 kHz mono Int16) once.</summary>
    Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default);

    /// <summary>Send a user turn to the current backend.</summary>
    Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default);

    /// <summary>Send a text command to the current backend.</summary>
    Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default);

    ISttClient Stt { get; }
}

public sealed class VoiceBackendManager : IVoiceBackendManager
{
    private readonly Dictionary<VoiceBackendKind, IVoiceBackend> _backends;
    private readonly Dictionary<VoiceBackendKind, Task> _connectTasks = new();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly AliceVoiceOptions _options;
    private IVoiceBackend _current;

    public VoiceBackendManager(IEnumerable<IVoiceBackend> backends, ISttClient stt, AliceVoiceOptions? options = null)
    {
        _options = options ?? new AliceVoiceOptions();
        Stt = stt;
        _backends = new Dictionary<VoiceBackendKind, IVoiceBackend>();
        foreach (var b in backends)
        {
            if (!_backends.TryAdd(b.Kind, b))
                throw new ArgumentException($"Duplicate backend for {b.Kind}", nameof(backends));
            b.EventReceived += e => EventReceived?.Invoke(e);
        }
        if (_backends.Count == 0) throw new ArgumentException("At least one backend is required", nameof(backends));
        _current = _backends.TryGetValue(VoiceBackendKind.AliceLocal, out var local) ? local : _backends.Values.First();
    }

    public ISttClient Stt { get; }
    public VoiceBackendKind CurrentKind => _current.Kind;
    public IVoiceBackend Current => _current;
    public IVoiceBackend Get(VoiceBackendKind kind) => _backends[kind];

    public event Action<BackendEvent>? EventReceived;

    public async Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default)
    {
        var next = _backends.TryGetValue(kind, out var b) ? b : throw new ArgumentOutOfRangeException(nameof(kind), kind, "No such backend");

        await _switchLock.WaitAsync(ct);
        try
        {
            if (_current.Kind == kind && _current.IsConnected && settings is null)
            {
                Log.Information("Already on backend {Kind}", kind);
                return;
            }

            // Connect once and keep warm. Reconnect when the last attempt
            // failed, was cancelled, or the backend has since dropped.
            if (settings is not null
                || !_connectTasks.TryGetValue(kind, out var connectTask)
                || connectTask.IsFaulted || connectTask.IsCanceled
                || (connectTask.IsCompletedSuccessfully && !next.IsConnected))
            {
                // The shared connect task must not be bound to one caller's token.
                connectTask = next.ConnectAsync(settings ?? VoiceBackendSettings.For(kind, _options), CancellationToken.None);
                _connectTasks[kind] = connectTask;
            }

            await connectTask.WaitAsync(ct);
            _current = next;

            Log.Information("Switched voice backend to {Kind}", kind);
            EventReceived?.Invoke(new BackendEvent("backend_switched", $"Backend: {kind}"));
        }
        finally { _switchLock.Release(); }
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await _switchLock.WaitAsync(ct);
        try { _connectTasks.Remove(_current.Kind); }
        finally { _switchLock.Release(); }
        await SwitchToAsync(_current.Kind, _current.Settings, ct);
    }

    public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default)
        => Stt.TranscribeAsync(pcm16k, ct);

    public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        => _current.AskAsync(prompt, options, ct);

    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
        => _current.SendCommandAsync(type, payload, ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _backends.Values)
        {
            try { await b.DisposeAsync(); } catch (Exception ex) { Log.Warning(ex, "Backend dispose failed"); }
        }
        _switchLock.Dispose();
    }
}
