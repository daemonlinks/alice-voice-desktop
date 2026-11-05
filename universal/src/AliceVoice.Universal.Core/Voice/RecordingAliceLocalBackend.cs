using AliceVoice.Core.Voice;

namespace AliceVoice.Universal.Voice;

/// <summary>
/// One Alice LOCAL backend for both screens. Asks are serialized here (and the
/// inner backend serializes too) so a phone turn and a window turn cannot run
/// at the same time. The reply is appended to <see cref="SharedTranscript"/>
/// before the caller continues, which is what makes the other screen see it.
/// </summary>
public sealed class RecordingAliceLocalBackend : IVoiceBackend, IAliceExtras
{
    private readonly IVoiceBackend _inner;
    private readonly SharedTranscript _transcript;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;

    public RecordingAliceLocalBackend(IVoiceBackend inner, SharedTranscript transcript, Func<DateTimeOffset>? clock = null)
    {
        _inner = inner;
        _transcript = transcript;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _inner.EventReceived += e => EventReceived?.Invoke(e);
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
    public VoiceBackendSettings Settings => _inner.Settings;
    public bool IsConnected => _inner.IsConnected;
    public SharedTranscript Transcript => _transcript;
    public event Action<BackendEvent>? EventReceived;

    public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
        => _inner.ConnectAsync(settings, ct);

    public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var reply = await _inner.AskAsync(prompt, options, ct);
            _transcript.Append(prompt, reply, ConversationOrigin.Current, _clock());
            return reply;
        }
        finally { _gate.Release(); }
    }

    public void ResetConversation()
    {
        _inner.ResetConversation();
        _transcript.Clear();
    }

    public async Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _inner.SendCommandAsync(type, payload, ct);
            if (type == "reset") _transcript.Clear();
        }
        finally { _gate.Release(); }
    }

    public Task<bool> RememberAsync(string content, CancellationToken ct = default)
    {
        if (_inner is AliceLocalBackend local) return local.RememberAsync(content, ct);
        if (_inner is IAliceExtras extras) return extras.RememberAsync(content, ct);
        return Task.FromResult(false);
    }

    public Task ReloadSoulAsync(CancellationToken ct = default)
    {
        if (_inner is AliceLocalBackend local) return local.ReloadSoulAsync(ct);
        if (_inner is IAliceExtras extras) return extras.ReloadSoulAsync(ct);
        return _inner.SendCommandAsync("reload_soul", null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        _gate.Dispose();
    }
}
