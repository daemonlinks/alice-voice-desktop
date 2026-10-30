using AliceVoice.Core.Stt;
using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

internal sealed class FakeStt : ISttClient
{
    public int Calls;
    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new SttResult($"{pcm16k.Length} samples", 1));
    }
    public bool IsReady => true;
    public string Status => "fake";
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeBackend(VoiceBackendKind kind) : IVoiceBackend
{
    public int ConnectCalls;
    public int FailNext;
    public TaskCompletionSource? Gate;
    public CancellationToken LastConnectToken;
    public VoiceBackendKind Kind { get; } = kind;
    public VoiceBackendSettings Settings { get; private set; } = VoiceBackendSettings.For(kind);
    public bool IsConnected { get; set; }
    public event Action<BackendEvent>? EventReceived;

    public async Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default)
    {
        ConnectCalls++;
        LastConnectToken = ct;
        if (Gate is not null) await Gate.Task;
        if (FailNext > 0) { FailNext--; throw new InvalidOperationException("down"); }
        Settings = settings;
        IsConnected = true;
        EventReceived?.Invoke(new BackendEvent("backend_ready", Kind.ToString()));
    }

    public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        => Task.FromResult($"{Kind}:{prompt}");
    public void ResetConversation() { }
    public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class VoiceBackendManagerTests
{
    private static (VoiceBackendManager mgr, FakeBackend local, FakeBackend ollama, FakeStt stt) Make()
    {
        var local = new FakeBackend(VoiceBackendKind.AliceLocal);
        var ollama = new FakeBackend(VoiceBackendKind.LocalOllama);
        var stt = new FakeStt();
        return (new VoiceBackendManager(new IVoiceBackend[] { local, ollama }, stt), local, ollama, stt);
    }

    [Fact]
    public void DefaultsToAliceLocal()
    {
        var (mgr, _, _, _) = Make();
        Assert.Equal(VoiceBackendKind.AliceLocal, mgr.CurrentKind);
    }

    [Fact]
    public async Task SwitchConnectsOnceAndKeepsWarm()
    {
        var (mgr, local, ollama, _) = Make();
        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        await mgr.SwitchToAsync(VoiceBackendKind.AliceLocal);
        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal(VoiceBackendKind.LocalOllama, mgr.CurrentKind);
        Assert.Equal(1, ollama.ConnectCalls);
        Assert.Equal(1, local.ConnectCalls);
    }

    [Fact]
    public async Task FailedSwitchLeavesCurrentUnchangedAndRetriesNextTime()
    {
        var (mgr, _, ollama, _) = Make();
        ollama.FailNext = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.SwitchToAsync(VoiceBackendKind.LocalOllama));
        Assert.Equal(VoiceBackendKind.AliceLocal, mgr.CurrentKind);

        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal(VoiceBackendKind.LocalOllama, mgr.CurrentKind);
        Assert.Equal(2, ollama.ConnectCalls);
    }

    [Fact]
    public async Task ReconnectsWhenBackendDropped()
    {
        var (mgr, local, _, _) = Make();
        await mgr.SwitchToAsync(VoiceBackendKind.AliceLocal);
        local.IsConnected = false;
        await mgr.SwitchToAsync(VoiceBackendKind.AliceLocal);
        Assert.Equal(2, local.ConnectCalls);
    }

    [Fact]
    public async Task ReconnectForcesNewConnect()
    {
        var (mgr, local, _, _) = Make();
        await mgr.SwitchToAsync(VoiceBackendKind.AliceLocal);
        await mgr.ReconnectAsync();
        Assert.Equal(2, local.ConnectCalls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonSharedConnect()
    {
        var (mgr, _, ollama, _) = Make();
        ollama.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var first = mgr.SwitchToAsync(VoiceBackendKind.LocalOllama, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(ollama.LastConnectToken.CanBeCanceled);

        ollama.Gate.SetResult();
        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal(1, ollama.ConnectCalls); // the original connect completed and was reused
        Assert.Equal(VoiceBackendKind.LocalOllama, mgr.CurrentKind);
    }

    [Fact]
    public async Task AskAndTranscribeRouteCorrectly()
    {
        var (mgr, _, _, stt) = Make();
        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Equal("LocalOllama:hi", await mgr.AskAsync("hi"));
        var r = await mgr.TranscribeAsync(new short[16000]);
        Assert.Equal("16000 samples", r.Text);
        Assert.Equal(1, stt.Calls);
    }

    [Fact]
    public async Task EventsAreForwarded()
    {
        var (mgr, _, _, _) = Make();
        var seen = new List<string>();
        mgr.EventReceived += e => seen.Add(e.Type);
        await mgr.SwitchToAsync(VoiceBackendKind.LocalOllama);
        Assert.Contains("backend_ready", seen);
        Assert.Contains("backend_switched", seen);
    }

    [Fact]
    public void DuplicateBackendsRejected()
    {
        Assert.Throws<ArgumentException>(() => new VoiceBackendManager(
            new IVoiceBackend[] { new FakeBackend(VoiceBackendKind.AliceLocal), new FakeBackend(VoiceBackendKind.AliceLocal) }, new FakeStt()));
    }

    [Fact]
    public void AgentSettingsUseHermesApiPort()
    {
        var s = VoiceBackendSettings.For(VoiceBackendKind.AliceVoiceAgent);
        Assert.Contains(":8643", s.AgentEndpoint);
        Assert.Equal("hermes", s.Provider);
    }
}
