using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using AliceVoice.Core.WakeWord;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using AliceVoice.Universal.Voice;
using AliceVoice.Universal.Wake;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class VoiceSessionTests
{
    [Fact]
    public async Task PushToTalkTranscribesOnceAndUsesAliceLocalStyle()
    {
        var fx = await Fixture.Start();
        var utc = DateTime.UtcNow;
        fx.Clock.Utc = utc;
        await fx.Session.PressTalkAsync();
        fx.Session.OnMicFrame(Enumerable.Repeat(0.2f, 8000).ToArray());
        fx.Clock.Utc = utc.AddSeconds(1);
        await fx.Session.ReleaseTalkAsync();

        Assert.Equal("hello there", fx.Manager.LastPrompt);
        Assert.Equal(160, fx.Manager.LastOptions!.MaxTokens);
        Assert.Contains("playful", fx.Manager.LastOptions.StyleHint);
        Assert.Contains("1–2", fx.Manager.LastOptions.StyleHint);
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.User && t.Text.Contains("hello there"));
        Assert.Contains("Sapi:Hello back", fx.Speech.Spoken);
        Assert.Equal("Replied from Alice LOCAL", fx.Session.StatusText);
        Assert.Equal(1, fx.Session.Latency.Items.Count);
    }

    [Fact]
    public async Task ShortTapDoesNotTranscribe()
    {
        var fx = await Fixture.Start();
        var utc = DateTime.UtcNow;
        fx.Clock.Utc = utc;
        await fx.Session.PressTalkAsync();
        fx.Clock.Utc = utc.AddMilliseconds(100);
        await fx.Session.ReleaseTalkAsync();
        Assert.Equal(PushToTalkGesture.State.Latched, fx.Session.TalkState);
        Assert.Null(fx.Manager.LastPrompt);
        Assert.Equal("■ Send", fx.Session.TalkButtonText);
    }

    [Fact]
    public async Task FleetTurnDoesNotSendStyleAndDoesNotUseTheLocalBackend()
    {
        var fx = await Fixture.Start();
        await fx.Session.SelectTargetAsync("alice_voice");
        await fx.Session.SendTextAsync("ping");
        Assert.Null(fx.Manager.LastPrompt);
        Assert.Equal(new[] { "alice_voice:ping" }, fx.Chat.Sent);
        Assert.Contains(fx.Session.Transcript, t => t.Text == "fleet-hi");
        Assert.False(fx.Session.StyleEnabled);
    }

    [Fact]
    public async Task SoulModeIsRefusedForFleetMembers()
    {
        var fx = await Fixture.Start();
        await fx.Session.SelectTargetAsync("alice_2");
        fx.Session.SetMode("soul");
        Assert.Equal("ptt", fx.Session.Mode);
        Assert.Equal(SessionCopy.SoulFleetStatus, fx.Session.StatusText);
        Assert.Contains(fx.Session.Transcript, t => t.Text == SessionCopy.SoulFleetNotice);
    }

    [Fact]
    public async Task RememberTruncatesToOneThousandCharacters()
    {
        var fx = await Fixture.Start();
        var entry = new TranscriptEntry { Text = new string('x', 1500), RememberEnabled = true };
        await fx.Session.RememberAsync(entry);
        Assert.Equal(1000, fx.Manager.Local.Remembered.Single().Length);
        Assert.Equal("♥ saved", entry.RememberLabel);
    }

    [Fact]
    public async Task WakeModeFallsBackWhenNoModelIsInstalled()
    {
        var fx = await Fixture.Start(wake: false);
        fx.Session.SetMode("wake");
        Assert.Equal("ptt", fx.Session.Mode);
        Assert.False(fx.Session.WakeAvailable);
        Assert.False(fx.Session.ContextSlidersEnabled);
        Assert.Equal(SessionCopy.ContextSliderReason, fx.Session.ContextSliderReason);
    }

    [Fact]
    public async Task WakeUtteranceUsesTheSameTurnPath()
    {
        var fx = await Fixture.Start();
        fx.Session.SetMode("wake");
        fx.Wake.RaiseUtterance(new short[1600]);
        await WaitUntil(() => fx.Session.StatusText.StartsWith("Replied", StringComparison.Ordinal));
        Assert.Equal("hello there", fx.Manager.LastPrompt);
        Assert.Contains("Wake listening: hey_alice", string.Join("\n", fx.Session.Logs));
    }

    [Fact]
    public async Task ResetClearsTheTranscriptAndTheBackend()
    {
        var fx = await Fixture.Start();
        await fx.Session.SendTextAsync("hi");
        await fx.Session.ResetAsync();
        Assert.Contains("reset", fx.Manager.Commands);
        Assert.Contains(fx.Session.Transcript, t => t.Text == "Conversation reset");
        Assert.DoesNotContain(fx.Session.Transcript, t => t.Kind == TranscriptKind.User);
    }

    [Fact]
    public async Task AndroidUpdateBannerDoesNotDownloadAWindowsPackage()
    {
        var fx = await Fixture.Start(kind: PlatformHostKind.Android);
        Assert.Contains("Windows package", fx.Session.UpdateBanner);
        Assert.Equal(0, fx.Updater.Downloads);
    }

    [Fact]
    public async Task TurnFailureSurvivesAThrowingPainter()
    {
        var fx = await Fixture.Start();
        fx.Manager.AskException = new HttpRequestException("ollama refused the connection");
        fx.Session.Changed += () => throw new KeyNotFoundException("The resource 'Chip' is not present in the dictionary.");

        await fx.Session.SendTextAsync("hello");

        Assert.Contains("ollama refused the connection", fx.Session.Error);
        Assert.DoesNotContain("Chip", fx.Session.Error);
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.System && t.Text.Contains("ollama refused the connection"));
        Assert.Contains(fx.Session.Logs, line => line.Contains("ollama refused the connection") && !line.Contains("Chip"));
    }

    [Fact]
    public async Task ReplyRemainsWhenPaintingTheRememberButtonThrows()
    {
        var fx = await Fixture.Start();
        fx.Session.Changed += () =>
        {
            if (fx.Session.Transcript.Any(t => t.ShowRemember))
                throw new KeyNotFoundException("The resource 'Chip' is not present in the dictionary.");
        };

        await fx.Session.SendTextAsync("hello");

        Assert.Null(fx.Session.Error);
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.Alice && t.Text == "Hello back" && t.ShowRemember);
        Assert.Equal("Replied from Alice LOCAL", fx.Session.StatusText);
    }

    [Fact]
    public async Task ErrorBannerClearsAfterTwelveSeconds()
    {
        var fx = await Fixture.Start();
        fx.Manager.Fail = true;
        await fx.Session.ConnectAsync(force: true);
        Assert.Contains("Connect failed", fx.Session.Error);
        fx.Clock.Utc = fx.Clock.Utc.AddSeconds(13);
        fx.Session.OnUiTick();
        Assert.Null(fx.Session.Error);
    }

    [Fact]
    public async Task SensitivityIsOneMinusThreshold()
    {
        var fx = await Fixture.Start();
        fx.Session.SetSensitivity(0.8);
        Assert.Equal(0.2f, fx.Wake.Threshold);
        Assert.Equal("0.80 (threshold 0.20)", fx.Session.SensitivityText);
    }

    [Fact]
    public async Task VoiceFileRoundTripsAsTheDesktopJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "alice-voice-assign-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new VoiceAssignmentStore(path);
        store.Save(new Dictionary<string, VoiceProfile> { ["alice_local"] = new(VoiceProviderKind.AllTalk, "female_06.wav") });
        var json = File.ReadAllText(path);
        Assert.Contains("\"Provider\": 0", json);
        var loaded = store.Load();
        Assert.Equal(VoiceProviderKind.AllTalk, loaded["alice_local"].Provider);
        Assert.Equal("female_06.wav", loaded["alice_local"].Voice);
        File.Delete(path);
    }

    [Fact]
    public void BackgroundsStepAndRandomise()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "alice-bg-" + Guid.NewGuid().ToString("N")));
        File.WriteAllBytes(Path.Combine(dir.FullName, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir.FullName, "b.png"), new byte[] { 2 });
        var catalog = BackgroundCatalog.Load(dir.FullName);
        Assert.Equal(2, catalog.Files.Count);
        Assert.EndsWith("a.jpg", catalog.Current);
        catalog.Step(1);
        Assert.EndsWith("b.png", catalog.Current);
        catalog.RandomOther();
        Assert.EndsWith("a.jpg", catalog.Current);
        dir.Delete(true);
    }

    [Fact]
    public async Task SuccessfulReconnectClearsTheError()
    {
        var fx = await Fixture.Start();
        fx.Manager.Fail = true;
        await fx.Session.ConnectAsync(force: true);
        Assert.Contains("Connect failed", fx.Session.Error);
        fx.Manager.Fail = false;
        await fx.Session.ConnectAsync(force: true);
        Assert.Null(fx.Session.Error);
        Assert.True(fx.Session.Connected);
        Assert.Equal("Alice LOCAL", fx.Session.StatusText);
    }

    [Fact]
    public async Task FailedAskKeepsTheQuestionWhenTheSharedTranscriptUpdates()
    {
        var fx = await Fixture.Start(shared: true);
        fx.Manager.AskException = new TimeoutException("Ollama did not finish within 3 minutes.");
        await fx.Session.SendTextAsync("where did I leave the question");
        Assert.Contains("3 minutes", fx.Session.Error);
        fx.Conversation!.Append("from the phone", "phone reply", "phone", DateTimeOffset.UtcNow);
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.User && t.Text.Contains("where did I leave the question"));
        Assert.Contains(fx.Session.Transcript, t => t.Text == "phone reply");
    }

    [Fact]
    public async Task DisconnectStopsTheTurnAndKeepsTheQuestion()
    {
        var fx = await Fixture.Start(shared: true);
        fx.Manager.AskEntered = new TaskCompletionSource<bool>();
        fx.Manager.ReleaseAsk = new TaskCompletionSource<bool>();
        var send = fx.Session.SendTextAsync("do not lose this");
        Assert.True(await fx.Manager.AskEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        fx.Session.Disconnect();
        await send;
        Assert.False(fx.Session.Connected);
        Assert.Equal("Ready", fx.Session.StatusText);
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.User && t.Text.Contains("do not lose this"));
    }

    [Fact]
    public async Task ANewQuestionCancelsThePreviousOneWithoutDroppingIt()
    {
        var fx = await Fixture.Start();
        fx.Manager.AskEntered = new TaskCompletionSource<bool>();
        var held = new TaskCompletionSource<bool>();
        fx.Manager.ReleaseAsk = held;
        var first = fx.Session.SendTextAsync("first question");
        Assert.True(await fx.Manager.AskEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        fx.Manager.ReleaseAsk = CompletedSource();
        await fx.Session.SendTextAsync("second question");
        await first;
        Assert.Contains(fx.Session.Transcript, t => t.Text.Contains("first question"));
        Assert.Contains(fx.Session.Transcript, t => t.Text.Contains("second question"));
        Assert.Contains(fx.Session.Transcript, t => t.Kind == TranscriptKind.Alice && t.Text == "Hello back");
        Assert.Equal("Replied from Alice LOCAL", fx.Session.StatusText);
    }

    [Fact]
    public async Task ConnectNamesEveryBridgeHostItTried()
    {
        var endpoint = new BridgeEndpoint("ahc-the-beast", 8765);
        var settings = new BridgeClientSettings(endpoint, null, new AliceVoiceOptions());
        settings.Probe = (_, _, _) => Task.FromResult(new ProbeResult(false, "connection refused"));
        var fx = await Fixture.Start(bridge: settings);
        await fx.Session.ConnectAsync(force: true);
        Assert.False(fx.Session.Connected);
        Assert.Contains("Connect failed:", fx.Session.Error);
        Assert.Contains("ahc-the-beast:8765 (connection refused)", fx.Session.Error);
        Assert.Contains("100.64.237.3:8765 (connection refused)", fx.Session.Error);
    }

    [Fact]
    public async Task ToolActivityShowsTheNameAndNotThePayload()
    {
        var fx = await Fixture.Start();
        fx.Manager.Raise(new BackendEvent("tool_call", "honcho_search"));
        Assert.Equal("using honcho_search", fx.Session.ActivityDetail);
        fx.Manager.Raise(new BackendEvent("tool_call", "{\"query\":\"secret fact\"}"));
        Assert.Equal("using a tool", fx.Session.ActivityDetail);
        Assert.DoesNotContain("secret", fx.Session.ActivityDetail);
    }

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = new TaskCompletionSource<bool>();
        source.SetResult(true);
        return source;
    }

    [Fact]
    public void LatencyBookKeepsTwentyTurns()
    {
        var book = new LatencyBook();
        Assert.Equal("waiting for data", book.Summary);
        for (var i = 0; i < 21; i++) book.Add(new TurnTiming("Alice LOCAL", i, i, i, i, i));
        Assert.Equal(20, book.Items.Count);
        Assert.Equal(1, book.Items[0].SttMs);
        Assert.Contains("20 turns", book.Summary);
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        for (var i = 0; i < 50 && !ready(); i++) await Task.Delay(10);
        Assert.True(ready());
    }

    private sealed class Fixture
    {
        public required VoiceSession Session { get; init; }
        public required FakeManager Manager { get; init; }
        public required FakeChat Chat { get; init; }
        public required FakeSpeechManager Speech { get; init; }
        public required FakeUpdater Updater { get; init; }
        public required FakeWake Wake { get; init; }
        public required MutableClock Clock { get; init; }
        public SharedTranscript? Conversation { get; init; }

        public static async Task<Fixture> Start(bool wake = true, PlatformHostKind kind = PlatformHostKind.Windows, bool shared = false, BridgeClientSettings? bridge = null)
        {
            var clock = new MutableClock { Utc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc) };
            var manager = new FakeManager();
            var chat = new FakeChat();
            var speech = new FakeSpeechManager();
            var updater = new FakeUpdater
            {
                Next = kind == PlatformHostKind.Android
                    ? new ReleaseInfo("0.2.0", "https://example.com/AliceVoice.App.msix", new string('b', 64))
                    : null
            };
            var wakeCtrl = new FakeWake { IsAvailable = wake, UnavailableReason = wake ? "" : "No wake-word model installed." };
            var host = new UniversalHost(kind, new AliceVoiceOptions(), Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "no-such-assignments.json"), Path.GetTempPath(), null, null, null);
            var conversation = shared ? new SharedTranscript() : null;
            var session = new VoiceSession(host, manager, chat, speech, new FakeMic(), updater, new FakeSoul(), new StaticWakeSource(wakeCtrl),
                new VoiceAssignmentStore(host.AssignmentPath), conversation, hub: null, clock: () => clock.Utc, localClock: () => clock.Utc, bridgeClient: bridge);
            session.SetMood("playful");
            session.SetProfile("quick");
            await session.InitializeAsync();
            return new Fixture { Session = session, Manager = manager, Chat = chat, Speech = speech, Updater = updater, Wake = wakeCtrl, Clock = clock, Conversation = conversation };
        }
    }

    private sealed class MutableClock { public DateTime Utc; }

    private sealed class FakeMic : IPcmMicrophone
    {
        public event Action<float[]>? AudioFrameCaptured;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStt : ISttClient
    {
        public bool IsReady => true;
        public string Status => "faster-whisper large-v3 · GPU 1";
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => Task.FromResult(new SttResult("hello there", 12));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeBackend : IVoiceBackend, IAliceExtras
    {
        public FakeBackend(VoiceBackendKind kind) { Kind = kind; Settings = VoiceBackendSettings.For(kind); }
        public VoiceBackendKind Kind { get; }
        public VoiceBackendSettings Settings { get; private set; }
        public bool IsConnected { get; set; }
        public List<string> Remembered { get; } = new();
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        public Task ConnectAsync(VoiceBackendSettings settings, CancellationToken ct = default) { IsConnected = true; Settings = settings; return Task.CompletedTask; }
        public Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default) => Task.FromResult("unused");
        public void ResetConversation() { }
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RememberAsync(string content, CancellationToken ct = default) { Remembered.Add(content); return Task.FromResult(true); }
        public Task ReloadSoulAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeManager : IVoiceBackendManager
    {
        public FakeBackend Local { get; } = new(VoiceBackendKind.AliceLocal);
        public FakeStt SttClient { get; } = new();
        public string? LastPrompt;
        public AskOptions? LastOptions;
        public string Reply = "Hello back";
        public List<string> Commands { get; } = new();
        public bool Fail;
        public Exception? AskException;
        public TaskCompletionSource<bool>? AskEntered;
        public TaskCompletionSource<bool>? ReleaseAsk;
        public VoiceBackendKind CurrentKind => VoiceBackendKind.AliceLocal;
        public IVoiceBackend Current => Local;
        public ISttClient Stt => SttClient;
        public event Action<BackendEvent>? EventReceived;
        public IVoiceBackend Get(VoiceBackendKind kind) => Local;
        public Task SwitchToAsync(VoiceBackendKind kind, VoiceBackendSettings? settings = null, CancellationToken ct = default)
        {
            Local.IsConnected = true;
            return Task.CompletedTask;
        }
        public Task ReconnectAsync(CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("down");
            return Task.CompletedTask;
        }
        public Task<SttResult> TranscribeAsync(short[] pcm16k, CancellationToken ct = default) => SttClient.TranscribeAsync(pcm16k, ct);
        public async Task<string> AskAsync(string prompt, AskOptions? options = null, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            LastOptions = options;
            AskEntered?.TrySetResult(true);
            if (ReleaseAsk is not null) await ReleaseAsk.Task.WaitAsync(ct);
            if (AskException is not null) throw AskException;
            EventReceived?.Invoke(new BackendEvent("reply_partial", "He"));
            return Reply;
        }

        public void Raise(BackendEvent evt) => EventReceived?.Invoke(evt);
        public Task SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
        {
            Commands.Add(type);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeChat : IFleetChat
    {
        public bool HasApiKey { get; set; } = true;
        public List<string> Sent { get; } = new();
        public Task<string> SendAsync(FleetMember member, string message, CancellationToken ct = default)
        {
            Sent.Add(member.Name + ":" + message);
            return Task.FromResult("fleet-hi");
        }
        public Task<bool> IsReachableAsync(FleetMember member, CancellationToken ct = default) => Task.FromResult(true);
        public void ResetConversation(FleetMember? member = null) { }
    }

    private sealed class FakeSpeechManager : IVoiceProviderManager
    {
        private readonly FakeProvider _sapi = new();
        public IReadOnlyList<IVoiceProvider> Providers => new[] { _sapi };
        public VoiceAssignment Assignment { get; } = new();
        public List<string> Spoken { get; } = new();
        public Task SpeakForPersonaAsync(string persona, string text, CancellationToken ct = default) => SpeakAsync(VoiceProfile.Default, text, ct);
        public Task SpeakForBackendAsync(VoiceBackendKind backend, string text, CancellationToken ct = default) => SpeakAsync(VoiceProfile.Default, text, ct);
        public Task SpeakAsync(VoiceProfile profile, string text, CancellationToken ct = default)
        {
            Spoken.Add(profile.Provider + ":" + text);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvider : IVoiceProvider
    {
        public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
        public string DisplayName => "SAPI";
        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "female_06.wav", "male.wav" });
        public Task SpeakAsync(string text, string voice, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public string CurrentVersion => "0.1.0";
        public ReleaseInfo? Next;
        public int Downloads;
        public Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default) => Task.FromResult(Next);
        public Task<StagedUpdate> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default)
        {
            Downloads++;
            throw new InvalidOperationException("downloads are not automatic");
        }
        public Task ApplyUpdateAsync(StagedUpdate staged, CancellationToken ct = default) => throw new InvalidOperationException("no launch");
    }

    private sealed class FakeSoul : ISoulGateway
    {
        public Task<SoulDocument> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SoulDocument("soul", "/tmp/SOUL.md", false));
        public Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default) => Task.FromResult(new SoulSaveResult("/tmp/SOUL.md", "/tmp/backups/SOUL.x.md"));
    }

    private sealed class FakeWake : IWakeController
    {
        public bool IsAvailable { get; set; }
        public string UnavailableReason { get; set; } = "";
        public string Phrase { get; set; } = "hey alice";
        public bool Interim { get; set; }
        public string ModelId { get; set; } = "hey_alice";
        public WakeWordSessionState State { get; set; } = WakeWordSessionState.Paused;
        public float LastScore { get; set; }
        public float Threshold { get; set; } = 0.5f;
        public void SetThreshold(float threshold) => Threshold = threshold;
        public void Enqueue(float[] pcm) { }
        public void Pause() => State = WakeWordSessionState.Paused;
        public void Resume() => State = WakeWordSessionState.Listening;
        public event Action<float>? WakeDetected;
        public event Action<short[]>? UtteranceReady;
        public event Action? UtteranceAbandoned;
        public event Action<Exception>? Faulted;
        public void Dispose() { }
        public void RaiseUtterance(short[] pcm) => UtteranceReady?.Invoke(pcm);
    }
}
