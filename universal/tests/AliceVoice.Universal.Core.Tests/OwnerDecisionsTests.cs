using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using AliceVoice.Universal.Updates;
using AliceVoice.Universal.Voice;
using AliceVoice.Universal.Wake;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class BridgeBindPolicyTests
{
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("ahc-the-beast")]
    [InlineData("beast.tailnet.ts.net")]
    public void TailscaleAddressesAreAccepted(string host)
    {
        var decision = BridgeBindPolicy.Evaluate(host, allowUnsafe: false);
        Assert.True(decision.Allowed);
        Assert.False(decision.Warned);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.20")]
    [InlineData("10.1.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.1.1")]
    [InlineData("public.example.com")]
    public void NonTailscaleBindsAreRefused(string host)
    {
        var decision = BridgeBindPolicy.Evaluate(host, allowUnsafe: false);
        Assert.False(decision.Allowed);
        Assert.Contains("not", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverrideAllowsALanBindAndSaysSo()
    {
        var decision = BridgeBindPolicy.Evaluate("192.168.0.8", allowUnsafe: true);
        Assert.True(decision.Allowed);
        Assert.True(decision.Warned);
        Assert.Contains("192.168", decision.Reason);
    }

    [Fact]
    public void PrepareRefusesALanBindUnlessTheFlagIsSet()
    {
        var refused = UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), key => key == "ALICEVOICE_BRIDGE_BIND" ? "192.168.1.5:8765" : null);
        Assert.False(refused.BridgeEnabled);
        Assert.Contains("192.168", refused.BridgeBindRefusal);

        var allowed = UniversalHost.Prepare(PlatformHostKind.Windows, Layout(), key => key switch
        {
            "ALICEVOICE_BRIDGE_BIND" => "192.168.1.5:8765",
            "ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND" => "1",
            _ => null
        });
        Assert.True(allowed.BridgeEnabled);
        Assert.Equal("192.168.1.5", allowed.BridgeBindHost);
    }

    [Fact]
    public void AndroidDefaultsToTheBeastTailscaleAddress()
    {
        var host = UniversalHost.Prepare(PlatformHostKind.Android, Layout(), _ => null);
        Assert.Equal(BridgeClientDirectory.TailscaleHost, host.Remote!.Host);
        Assert.Equal(8765, host.Remote.BridgePort);
        Assert.False(host.BridgeEnabled);
        Assert.Equal("http://100.64.237.3:11434", host.Options.OllamaUrl);
        Assert.Equal("http://100.64.237.3:7851", host.Options.AllTalkUrl);
    }

    private static HostLayout Layout() => new(null, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "voices.json"), Path.GetTempPath());
}

public class AndroidUpdateSchemaTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void WindowsSelectionIgnoresTheAndroidBlock()
    {
        var json = Manifest("0.2.0", "https://example.test/AliceVoice.App.msix", "0.3.0", "https://example.test/AliceVoice.AppU.apk");
        var release = HttpAppUpdater.SelectPackage(json, "0.1.0", UpdatePackagePreference.Windows);
        Assert.NotNull(release);
        Assert.EndsWith(".msix", release!.DownloadUrl);
        Assert.Equal("0.2.0", release.Version);
    }

    [Fact]
    public void AndroidSelectionOffersTheApkWhenItIsNewer()
    {
        var json = Manifest("0.2.0", "https://example.test/AliceVoice.App.msix", "0.3.0", "https://example.test/AliceVoice.AppU.apk");
        var release = HttpAppUpdater.SelectPackage(json, "0.2.0", UpdatePackagePreference.Android);
        Assert.NotNull(release);
        Assert.EndsWith(".apk", release!.DownloadUrl);
        Assert.Equal("0.3.0", release.Version);
        var offer = UpdateOffer.For(release, PlatformHostKind.Android);
        Assert.Contains("Android APK", offer.Banner);
        Assert.False(offer.CanInstall);
    }

    [Fact]
    public void AndroidWithoutAnApkFallsBackToTheWindowsFields()
    {
        var json = $$"""{"version":"0.4.0","downloadUrl":"https://example.test/AliceVoice.App.msix","sha256":"{{Sha}}"}""";
        var release = HttpAppUpdater.SelectPackage(json, "0.1.0", UpdatePackagePreference.Android);
        Assert.NotNull(release);
        Assert.True(UpdateOffer.IsWindowsPackage(release!.DownloadUrl));
        Assert.Contains("Windows package", UpdateOffer.For(release, PlatformHostKind.Android).Banner);
    }

    private static string Manifest(string winVersion, string winUrl, string apkVersion, string apkUrl)
        => "{\"version\":\"" + winVersion + "\",\"downloadUrl\":\"" + winUrl + "\",\"sha256\":\"" + Sha
           + "\",\"android\":{\"version\":\"" + apkVersion + "\",\"downloadUrl\":\"" + apkUrl + "\",\"sha256\":\"" + Sha + "\"}}";
}

public class VoiceAssignmentKindTests
{
    [Fact]
    public void AndroidSpeechIsAppendedWithoutRenumbering()
    {
        Assert.Equal(0, (int)VoiceProviderKind.AllTalk);
        Assert.Equal(1, (int)VoiceProviderKind.Sapi);
        Assert.Equal(2, (int)VoiceProviderKind.Piper);
        Assert.Equal(3, (int)VoiceProviderKind.AndroidSpeech);
    }

    [Fact]
    public void AssignmentJsonRoundTripsTheNewValue()
    {
        var path = Path.Combine(Path.GetTempPath(), "assign-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new VoiceAssignmentStore(path);
        store.Save(new Dictionary<string, VoiceProfile>
        {
            ["alice_local"] = new(VoiceProviderKind.AllTalk, "female_06.wav"),
            ["alice_voice"] = new(VoiceProviderKind.AndroidSpeech, "en-GB")
        });
        var json = File.ReadAllText(path);
        Assert.Contains("\"Provider\": 0", json);
        Assert.Contains("\"Provider\": 3", json);
        var loaded = store.Load();
        Assert.Equal(VoiceProviderKind.AndroidSpeech, loaded["alice_voice"].Provider);
        Assert.Equal("en-GB", loaded["alice_voice"].Voice);
        File.Delete(path);
    }

    [Fact]
    public async Task WindowsIgnoresAnAndroidAssignment()
    {
        var path = Path.Combine(Path.GetTempPath(), "assign-" + Guid.NewGuid().ToString("N") + ".json");
        new VoiceAssignmentStore(path).Save(new Dictionary<string, VoiceProfile>
        {
            ["alice_local"] = new(VoiceProviderKind.AndroidSpeech, "en-GB")
        });
        var manager = new IgnoringSpeech();
        var host = new UniversalHost(PlatformHostKind.Windows, new AliceVoiceOptions(), Path.GetTempPath(), path, Path.GetTempPath(), null, null, null);
        var session = new VoiceSession(host, new QuietManager(), new QuietChat(), manager, new QuietMic(), new QuietUpdater(), new QuietSoul(),
            new StaticWakeSource(new UnavailableWakeController("none")), new VoiceAssignmentStore(path));
        await session.InitializeAsync();
        Assert.Equal(VoiceProviderKind.Sapi, session.SelectedProvider);
        Assert.Contains("not on this device", string.Join("\n", session.Logs));
        Assert.Contains("\"Provider\": 3", File.ReadAllText(path));
        await manager.SpeakAsync(new VoiceProfile(VoiceProviderKind.AndroidSpeech, "en-GB"), "hello");
        File.Delete(path);
    }

    [Fact]
    public void SpeakingAnUnregisteredKindDoesNotThrow()
    {
        var manager = new VoiceProviderManager(new[] { new SapiOnly() });
        manager.SpeakAsync(new VoiceProfile(VoiceProviderKind.AndroidSpeech, "en-GB"), "hello").GetAwaiter().GetResult();
    }

    private sealed class SapiOnly : IVoiceProvider
    {
        public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
        public string DisplayName => "SAPI";
        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(new[] { "Hazel" });
        public Task SpeakAsync(string text, string voice, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class IgnoringSpeech : IVoiceProviderManager
    {
        private readonly SapiOnly _sapi = new();
        public IReadOnlyList<IVoiceProvider> Providers => new[] { _sapi };
        public VoiceAssignment Assignment { get; } = new();
        public Task SpeakForPersonaAsync(string persona, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SpeakForBackendAsync(VoiceBackendKind backend, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SpeakAsync(VoiceProfile profile, string text, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class QuietMic : AliceVoice.Universal.Audio.IPcmMicrophone
    {
        public event Action<float[]>? AudioFrameCaptured { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QuietSoul : ISoulGateway
    {
        public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SoulDocument("s", "/tmp/SOUL.md", false));
        public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => Task.FromResult(new SoulSaveResult("/tmp/SOUL.md", null));
    }

    private sealed class QuietUpdater : IAppUpdater
    {
        public string CurrentVersion => "0.1.0";
        public Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
        public Task<StagedUpdate> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ApplyUpdateAsync(StagedUpdate staged, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class QuietChat : IFleetChat
    {
        public bool HasApiKey => false;
        public Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default) => Task.FromResult(false);
        public void ResetConversation(FleetMember? member = null) { }
    }

    private sealed class QuietManager : IVoiceBackendManager
    {
        private readonly QuietBackend _local = new();
        public VoiceBackendKind CurrentKind => VoiceBackendKind.AliceLocal;
        public IVoiceBackend Current => _local;
        public ISttClient Stt { get; } = new QuietStt();
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public IVoiceBackend Get(VoiceBackendKind kind) => _local;
        public Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Stt.TranscribeAsync(pcm16k, ct);
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default) => Task.FromResult("ok");
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QuietBackend : IVoiceBackend
    {
        public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
        public VoiceBackendSettings Settings { get; } = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
        public bool IsConnected { get; private set; } = true;
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default) { IsConnected = true; return Task.CompletedTask; }
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default) => Task.FromResult("ok");
        public void ResetConversation() { }
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QuietStt : ISttClient
    {
        public bool IsReady => true;
        public string Status => "idle";
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Task.FromResult(new SttResult("", 0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class SharedConversationTests
{
    [Fact]
    public async Task ConcurrentAsksStayInOrderOnOneConversation()
    {
        var inner = new GateBackend();
        var store = new SharedTranscript();
        var shared = new RecordingAliceLocalBackend(inner, store);
        var first = shared.AskAsync("one", null);
        var second = shared.AskAsync("two", null);
        var replies = await Task.WhenAll(first, second);
        Assert.Equal(new[] { "reply:one", "reply:two" }, replies);
        var turns = store.Snapshot();
        Assert.Equal(new[] { "one", "two" }, turns.Select(t => t.User).ToArray());
        Assert.Equal(new[] { "one", "two" }, inner.Seen);
    }

    [Fact]
    public async Task PhoneTurnShowsInTheWindowAndResetClearsBoth()
    {
        var store = new SharedTranscript();
        var recording = new RecordingAliceLocalBackend(new EchoBackend(), store);
        var manager = new SharedManager(recording);
        var host = new UniversalHost(PlatformHostKind.Windows, new AliceVoiceOptions(), Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "none.json"), Path.GetTempPath(), null, null, null);
        var session = new VoiceSession(host, manager, new VoiceAssignmentKindTestsQuiet(), new SapiManager(), new NoMic(), new NoUpdater(), new NoSoul(),
            new StaticWakeSource(new UnavailableWakeController("none")), new VoiceAssignmentStore(host.AssignmentPath), store);
        await session.InitializeAsync();

        using (ConversationOrigin.Use(ConversationOrigin.Phone))
            await recording.AskAsync("from the phone", null);
        await session.SendTextAsync("from the pc");

        var text = string.Join("\n", session.Transcript.Select(t => t.Text));
        Assert.Contains("from the phone", text);
        Assert.Contains("from the pc", text);
        Assert.Equal(2, store.Snapshot().Count);

        await session.ResetAsync();
        Assert.Empty(store.Snapshot());
        Assert.DoesNotContain(session.Transcript, t => t.Text.Contains("from the phone"));
    }

    private sealed class GateBackend : IVoiceBackend
    {
        public List<string> Seen { get; } = new();
        public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
        public VoiceBackendSettings Settings { get; } = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
        public bool IsConnected => true;
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        {
            Seen.Add(prompt);
            await Task.Delay(30, ct);
            return "reply:" + prompt;
        }
        public void ResetConversation() { }
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EchoBackend : IVoiceBackend
    {
        public VoiceBackendKind Kind => VoiceBackendKind.AliceLocal;
        public VoiceBackendSettings Settings { get; } = VoiceBackendSettings.For(VoiceBackendKind.AliceLocal);
        public bool IsConnected => true;
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default) => Task.FromResult("echo " + prompt);
        public void ResetConversation() { }
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SharedManager : IVoiceBackendManager
    {
        private readonly RecordingAliceLocalBackend _local;
        public SharedManager(RecordingAliceLocalBackend local) => _local = local;
        public VoiceBackendKind CurrentKind => VoiceBackendKind.AliceLocal;
        public IVoiceBackend Current => _local;
        public ISttClient Stt { get; } = new IdleStt();
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public IVoiceBackend Get(VoiceBackendKind kind) => _local;
        public Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Stt.TranscribeAsync(pcm16k, ct);
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default) => _local.AskAsync(prompt, options, ct);
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => _local.SendCommandAsync(type, payload, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleStt : ISttClient
    {
        public bool IsReady => true;
        public string Status => "idle";
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Task.FromResult(new SttResult("", 0));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoMic : AliceVoice.Universal.Audio.IPcmMicrophone
    {
        public event Action<float[]>? AudioFrameCaptured { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoSoul : ISoulGateway
    {
        public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SoulDocument("s", "/tmp/SOUL.md", false));
        public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => Task.FromResult(new SoulSaveResult("/tmp/SOUL.md", null));
    }

    private sealed class NoUpdater : IAppUpdater
    {
        public string CurrentVersion => "0.1.0";
        public Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default) => Task.FromResult<ReleaseInfo?>(null);
        public Task<StagedUpdate> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ApplyUpdateAsync(StagedUpdate staged, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class SapiManager : IVoiceProviderManager
    {
        private readonly IVoiceProvider _sapi = new SapiStub();
        public IReadOnlyList<IVoiceProvider> Providers => new[] { _sapi };
        public VoiceAssignment Assignment { get; } = new();
        public Task SpeakForPersonaAsync(string persona, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SpeakForBackendAsync(VoiceBackendKind backend, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SpeakAsync(VoiceProfile profile, string text, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SapiStub : IVoiceProvider
    {
        public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
        public string DisplayName => "SAPI";
        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(new[] { "female_06.wav" });
        public Task SpeakAsync(string text, string voice, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Nested tests cannot see the private chat in the other fixture, so this is a tiny stand-in.</summary>
    private sealed class VoiceAssignmentKindTestsQuiet : IFleetChat
    {
        public bool HasApiKey => false;
        public Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default) => Task.FromResult(false);
        public void ResetConversation(FleetMember? member = null) { }
    }
}
