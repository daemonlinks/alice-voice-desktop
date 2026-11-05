using AliceVoice.Core.Chat;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Voice;

namespace AliceVoice.Universal.Bridge;

/// <summary>Phone Alice LOCAL. The tool loop, SOUL and Honcho run on the Beast; this only forwards the turn.</summary>
public sealed class BridgeAliceLocalBackend : IVoiceBackend, IAliceExtras
{
    private readonly BeastBridgeClient _client;

    public BridgeAliceLocalBackend(BeastBridgeClient client)
    {
        _client = client;
        Settings = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
    }

    public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
    public VoiceBackendSettings Settings { get; private set; }
    public bool IsConnected { get; private set; }
    public event Action<BackendEvent>? EventReceived;

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        IsConnected = false;
        await _client.LocalConnectAsync(ct);
        IsConnected = true;
        EventReceived?.Invoke(new BackendEvent("backend_ready", "Alice LOCAL ready — via the Beast bridge"));
    }

    public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        => _client.AskLocalAsync(prompt, options, evt => EventReceived?.Invoke(evt), ct);

    public void ResetConversation() { }

    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
        => _client.LocalCommandAsync(type, ct);

    public Task<bool> RememberAsync(string content, CancellationToken ct = default) => _client.RememberAsync(content, ct);

    public async Task ReloadSoulAsync(CancellationToken ct = default) => await _client.LocalCommandAsync("reload_soul", ct);

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Phone fleet chat. The Hermes API key is added on the Beast and never crosses the bridge.</summary>
public sealed class BridgeFleetChat : IFleetChat
{
    private readonly BeastBridgeClient _client;
    public BridgeFleetChat(BeastBridgeClient client) => _client = client;

    public bool HasApiKey { get; private set; }

    public async Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default)
        => await _client.FleetSendAsync(member.Name, message, ct);

    public async Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default)
    {
        var result = await _client.FleetReachableAsync(member.Name, ct);
        HasApiKey = result.HasApiKey;
        return result.Ok;
    }

    public void ResetConversation(FleetMember? member = null)
    {
        // Hop off the UI sync context so the HTTP call cannot deadlock it.
        Task.Run(() => _client.FleetResetAsync(member?.Name)).GetAwaiter().GetResult();
    }
}

public sealed class RemoteSoulGateway : ISoulGateway
{
    private readonly BeastBridgeClient _client;
    public RemoteSoulGateway(BeastBridgeClient client) => _client = client;
    public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => _client.GetSoulAsync(ct);
    public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => _client.SaveSoulAsync(text, ct);
}

public sealed class RemoteSttClient : ISttClient
{
    private readonly BeastBridgeClient _client;
    private string _status;

    public RemoteSttClient(BeastBridgeClient client)
    {
        _client = client;
        _status = $"Beast STT at {client.Urls.Stt}";
    }

    public bool IsReady { get; private set; }
    public string Status => _status;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        _status = await _client.WarmSttAsync(ct);
        IsReady = true;
    }

    public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default)
        => _client.TranscribeAsync(pcm16k, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Phone that has not been told where the Beast is. Speech-to-text cannot fall back onto a render GPU that is not there.</summary>
public sealed class UnconfiguredRemoteSttClient : ISttClient
{
    public const string Reason = "STT on this phone uses the Beast GPU. Set ALICEVOICE_BEAST_HOST (Tailscale) and run App U on the Beast with ALICEVOICE_BRIDGE_BIND.";
    public bool IsReady => false;
    public string Status => Reason;
    public Task WarmUpAsync(CancellationToken ct = default) => throw new SttException(Reason);
    public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => throw new SttException(Reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UnconfiguredAliceLocalBackend : IVoiceBackend
{
    public UnconfiguredAliceLocalBackend() => Settings = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
    public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
    public VoiceBackendSettings Settings { get; }
    public bool IsConnected => false;
    public event Action<BackendEvent>? EventReceived { add { } remove { } }
    public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
        => throw new InvalidOperationException(UnconfiguredRemoteSttClient.Reason.Replace("STT on this phone uses the Beast GPU.", "Alice LOCAL on this phone runs on the Beast."));
    public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        => throw new InvalidOperationException("Alice LOCAL is not connected.");
    public void ResetConversation() { }
    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
