using System.Diagnostics;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Logging;
using AliceVoice.Core.Voice;
using AliceVoice.Core.WakeWord;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Updates;
using AliceVoice.Universal.Voice;
using AliceVoice.Universal.Wake;
using Serilog;

namespace AliceVoice.Universal.Session;

public enum StageLamp { Dim, Green, Yellow, Red, Wait }
public enum NoteKind { Dim, Green, Yellow, Red }
public enum TranscriptKind { User, Alice, System }

public sealed class TranscriptEntry
{
    public TranscriptKind Kind { get; init; }
    public string Speaker { get; init; } = "";
    public string Text { get; set; } = "";
    public int Turn { get; init; }
    public DateTimeOffset At { get; init; }
    public bool ShowRemember { get; set; }
    public bool RememberEnabled { get; set; }
    public string RememberLabel { get; set; } = "♥ remember";
    /// <summary>Set on bubbles that belong to the shared Alice LOCAL conversation, so a refresh can replace them.</summary>
    public long? SharedId { get; init; }
    /// <summary>In-flight local bubble replaced when the canonical turn arrives.</summary>
    public bool PendingShared { get; set; }
}

public sealed record SessionTarget(string Id, string DisplayName);

/// <summary>
/// The WinUI window's behaviour, without WinUI: modes, push-to-talk, wake,
/// one shared turn path, SOUL, voice assignments, latency, backgrounds.
/// Both MAUI heads bind to this.
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    public const string AliceLocalId = "alice_local";

    private readonly UniversalHost _host;
    private readonly IVoiceBackendManager _voice;
    private readonly IFleetChat _chat;
    private readonly IVoiceProviderManager _providers;
    private readonly IPcmMicrophone _mic;
    private readonly IAppUpdater _updater;
    private readonly ISoulGateway _soul;
    private readonly IWakeSource _wakeSource;
    private readonly VoiceAssignmentStore _assignments;
    private readonly IAliceConversation? _conversation;
    private readonly SoulReloadHub? _hub;
    private readonly Func<DateTime> _clock;
    private readonly Func<DateTime> _local;
    private readonly PushToTalkGesture _ptt = new();
    private readonly UtteranceBuffer _utterance = new(minSeconds: 0.3, maxSeconds: 120);
    private readonly Dictionary<string, VoiceProfile> _memberVoices;
    private readonly List<TranscriptEntry> _transcript = new();
    private readonly List<string> _logs = new();
    private readonly object _gate = new();

    private IWakeController _wake = new UnavailableWakeController("Wake word is not open yet.");
    private CancellationTokenSource? _turnCts;
    private int _turnId;
    private int _streamTurnId;
    private TranscriptEntry? _streamEntry;
    private int _wakeBusy;
    private int _turnSeq;
    private bool _micRunning;
    private bool _initializing = true;
    private bool _restoring;
    private bool _capNoted;
    private float _vuLevel;
    private string _lastNonSoulMode = "ptt";
    private DateTime _errorUntil;
    private DateTime _nextConversationPoll;
    private Action? _stopSpeech;

    public VoiceSession(UniversalHost host, IVoiceBackendManager voice, IFleetChat chat, IVoiceProviderManager providers,
        IPcmMicrophone mic, IAppUpdater updater, ISoulGateway soul, IWakeSource wakeSource, VoiceAssignmentStore assignments,
        IAliceConversation? conversation = null, SoulReloadHub? hub = null, Func<DateTime>? clock = null, Func<DateTime>? localClock = null)
    {
        _host = host;
        _voice = voice;
        _chat = chat;
        _providers = providers;
        _mic = mic;
        _updater = updater;
        _soul = soul;
        _wakeSource = wakeSource;
        _assignments = assignments;
        _conversation = conversation;
        _hub = hub;
        if (_conversation is not null) _conversation.Changed += () => ApplySharedSnapshot();
        _clock = clock ?? (() => DateTime.UtcNow);
        _local = localClock ?? (() => DateTime.Now);
        _memberVoices = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        _mic.AudioFrameCaptured += OnMicFrame;
        _voice.EventReceived += OnVoiceEvent;
        SelectedTargetId = AliceLocalId;
        ProfileHint = SessionCopy.ProfileHint(Profile);
        SensitivityText = "0.50 (threshold 0.50)";
        Targets = new[] { new SessionTarget(AliceLocalId, "Alice LOCAL") }
            .Concat(FleetRoster.Members.Select(m => new SessionTarget(m.Name, m.DisplayName)))
            .ToList();
    }

    public event Action? Changed;
    public event Action? MeterChanged;

    public IReadOnlyList<SessionTarget> Targets { get; }
    public string SelectedTargetId { get; private set; }
    public bool IsAliceLocal => SelectedTargetId == AliceLocalId;
    public SessionTarget SelectedTarget => Targets.First(t => t.Id == SelectedTargetId);
    public FleetMember? SelectedMember => IsAliceLocal ? null : FleetRoster.Find(SelectedTargetId);
    public string TargetName => SelectedTarget.DisplayName;

    public string Mode { get; private set; } = "ptt";
    public string Mood { get; private set; } = "natural";
    public string Profile { get; private set; } = "natural";
    public string ProfileHint { get; private set; }
    public bool Connected { get; private set; }
    public bool StyleEnabled => IsAliceLocal;
    public bool ContextSlidersEnabled => false;
    public string ContextSliderReason => SessionCopy.ContextSliderReason;
    public bool WakeAvailable => _wake.IsAvailable;
    public string WakeUnavailableReason => _wake.UnavailableReason;
    public string WakeWordLabel => !_wake.IsAvailable
        ? "Wake word: —"
        : $"Wake word: “{_wake.Phrase}”{(_wake.Interim ? " (interim test model)" : "")}";
    public double Sensitivity { get; private set; } = 0.5;
    public string SensitivityText { get; private set; }
    public string WakeScoreText { get; private set; } = "";
    public string TalkButtonText { get; private set; } = "🎤 Talk";
    public string TalkTip => SessionCopy.TalkTip;
    public PushToTalkGesture.State TalkState => _ptt.Current;
    public bool TalkEnabled => Connected;
    public bool SendEnabled => Connected;
    public bool ResetEnabled => Connected;

    public string Activity { get; private set; } = "Ready";
    public string ActivityDetail { get; private set; } = "";
    public string StatusText { get; private set; } = "Ready";
    public string? Error { get; private set; }
    public string? UpdateBanner { get; private set; }
    public string MicTag { get; private set; } = "🎤 Off";
    public StageLamp MicTagLamp { get; private set; } = StageLamp.Red;
    public double VuWidth => VuMeter.WidthPx(_vuLevel);
    public StageLamp MicLamp { get; private set; } = StageLamp.Dim;
    public StageLamp SttLamp { get; private set; } = StageLamp.Dim;
    public StageLamp LlmLamp { get; private set; } = StageLamp.Dim;
    public StageLamp TtsLamp { get; private set; } = StageLamp.Dim;
    public StageLamp ApiLamp { get; private set; } = StageLamp.Dim;
    public string StageError { get; private set; } = "";

    public string AgentLabel { get; private set; } = "Alice LOCAL";
    public string BackendLabel { get; private set; } = "local (Ollama)";
    public string ProviderLabel { get; private set; } = "—";
    public string VoiceLabel { get; private set; } = "—";
    public string ConnectionLabel { get; private set; } = "offline";
    public string SettingsLine { get; private set; } = "";
    public string FooterAuth { get; private set; } = "—";
    public string LlmMode { get; private set; } = "alice_local";
    public string WsDisplay { get; private set; } = "api·off";
    public string Version => _updater.CurrentVersion;
    public string SoulText { get; private set; } = "";
    public string SoulStatus { get; private set; } = "";
    public NoteKind SoulKind { get; private set; } = NoteKind.Dim;
    public string? BackgroundPath => Backgrounds.Current;
    public BackgroundCatalog Backgrounds { get; private set; } = new(Array.Empty<string>());
    public LatencyBook Latency { get; } = new();
    public VoiceProviderKind? SelectedProvider { get; private set; }
    public string SelectedVoice { get; private set; } = "";
    public IReadOnlyList<string> VoiceNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<IVoiceProvider> Providers => _providers.Providers;
    public IReadOnlyList<TranscriptEntry> Transcript { get { lock (_gate) return _transcript.ToArray(); } }
    public IReadOnlyList<string> Logs { get { lock (_gate) return _logs.ToArray(); } }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!_initializing) return;
        OperationTrail.Leave("session.init");
        _wake = await _wakeSource.OpenAsync(ct);
        HookWake(_wake);
        if (_wake.IsAvailable)
            ApplySensitivity(WakeWordSetup.SensitivityFor(_wake.Threshold), writeEngine: false);
        Backgrounds = BackgroundCatalog.Load(_host.BackgroundsDirectory ?? _host.Options.ResolvedBackgroundsDir);
        try
        {
            if (File.Exists(_assignments.Path))
            {
                var loaded = _assignments.Load();
                _memberVoices.Clear();
                foreach (var (name, profile) in loaded) _memberVoices[name] = profile;
                AddLog($"Voice assignments loaded: {_memberVoices.Count}");
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load voice assignments"); }
        _initializing = false;
        await RestoreVoiceAsync();
        await CheckForUpdatesAsync();
        await WarmUpSttAsync();
        Append(TranscriptKind.System, "", SessionCopy.Startup, showRemember: false);
        RefreshStatus();
        await ConnectAsync(force: false, ct);
        OperationTrail.Leave("session.ready");
        Touch();
    }

    /// <summary>Stops device playback when a turn is cancelled (AllTalk / Piper buffer). SAPI stops because the turn token is cancelled.</summary>
    public void UseSpeechStop(Action stop) => _stopSpeech = stop;

    public async Task SelectTargetAsync(string id, CancellationToken ct = default)
    {
        if (_initializing || id == SelectedTargetId) return;
        if (Targets.All(t => t.Id != id)) return;
        CancelTurn();
        SelectedTargetId = id;
        if (Mode == "soul" && !IsAliceLocal) SetMode(_lastNonSoulMode);
        await RestoreVoiceAsync();
        await ConnectAsync(force: false, ct);
    }

    public async Task ConnectAsync(bool force = false, CancellationToken ct = default)
    {
        StatusText = "Connecting…";
        SetActivity("Connecting…");
        Touch();
        try
        {
            if (IsAliceLocal)
            {
                if (force && _voice.CurrentKind == VoiceBackendKind.AliceLocal) await _voice.ReconnectAsync(ct);
                else await _voice.SwitchToAsync(VoiceBackendKind.AliceLocal, ct: ct);
                SetConnected(true);
                StatusText = "Alice LOCAL";
                SetActivity("Ready", "Alice LOCAL · local (Ollama)");
            }
            else if (SelectedMember is { } member)
            {
                var ok = await _chat.IsReachableAsync(member, ct);
                SetConnected(ok);
                StatusText = ok
                    ? $"{member.DisplayName} reachable"
                    : $"{member.DisplayName} not reachable ({(_chat.HasApiKey ? "not served / key rejected" : "no API_SERVER_KEY")})";
                SetActivity(ok ? "Ready" : "Offline", member.DisplayName);
            }
        }
        catch (Exception ex)
        {
            SetConnected(false);
            StatusText = "Not connected";
            SetActivity("Offline", ex.Message);
            OperationTrail.Leave("session.connect.failed", ex.GetType().Name);
            LogFailure(ex, "Connect failed");
            ShowError($"Connect failed: {ex.Message}");
        }
        RefreshStatus();
        Touch();
    }

    public void Disconnect()
    {
        SetConnected(false);
        StatusText = "Ready";
        RefreshStatus();
        Touch();
    }

    public async Task ResetAsync()
    {
        CancelTurn();
        lock (_gate) _transcript.Clear();
        _turnSeq = 0;
        try
        {
            if (IsAliceLocal) await _voice.SendCommandAsync("reset");
            else _chat.ResetConversation(SelectedMember);
        }
        catch (Exception ex) { Log.Warning(ex, "Reset failed"); }
        StatusText = "Ready";
        Append(TranscriptKind.System, "", "Conversation reset", showRemember: false);
        Touch();
    }

    public void SetMode(string mode)
    {
        if (mode == "wake" && !_wake.IsAvailable) mode = "ptt";
        if (mode == "soul" && !IsAliceLocal)
        {
            StatusText = SessionCopy.SoulFleetStatus;
            Append(TranscriptKind.System, "", SessionCopy.SoulFleetNotice, showRemember: false);
            Touch();
            return;
        }
        if (mode != "soul" && Mode != "soul") _lastNonSoulMode = mode;
        var wasWake = Mode == "wake";
        Mode = mode;
        if (mode is not ("ptt" or "wake") && _ptt.Current != PushToTalkGesture.State.Idle)
        {
            _ptt.Reset();
            _utterance.Cancel();
            TalkButtonText = "🎤 Talk";
            _ = StopMicAsync();
        }
        AddLog($"Mode: {mode}");
        if (mode == "soul") _ = LoadSoulAsync();
        if (mode == "wake" && !wasWake) _ = StartWakeAsync();
        else if (mode != "wake" && wasWake) _ = StopWakeAsync();
        Touch();
    }

    public void SetMood(string mood)
    {
        if (!SessionCopyIsMood(mood)) return;
        Mood = mood;
        AddLog($"Mood → {mood}");
        Touch();
    }

    public void SetProfile(string profile)
    {
        if (profile is not ("quick" or "natural" or "full")) return;
        Profile = profile;
        ProfileHint = SessionCopy.ProfileHint(profile);
        AddLog($"Profile → {profile}");
        Touch();
    }

    public void SetSensitivity(double value) => ApplySensitivity(value, writeEngine: true);

    public async Task SetProviderAsync(VoiceProviderKind kind)
    {
        if (_initializing || _restoring) { SelectedProvider = kind; return; }
        SelectedProvider = kind;
        SelectedVoice = "";
        AddLog($"Voice provider: {kind}");
        await LoadVoicesAsync(null);
        RememberVoice();
        Touch();
    }

    public void SetVoice(string voice)
    {
        SelectedVoice = voice;
        if (!_initializing && !_restoring && SelectedProvider is { } kind)
        {
            _memberVoices[SelectedTargetId] = new VoiceProfile(kind, voice);
            SaveVoices();
        }
        AddLog($"Voice: {voice.Replace(".wav", "").Replace("_", " ")}");
        RefreshStatus();
        Touch();
    }

    public Task PressTalkAsync() => ApplyPttAsync(_ptt.Press(_clock()));
    public Task ReleaseTalkAsync() => ApplyPttAsync(_ptt.Release(_clock()));
    public Task ToggleTalkAsync() => ApplyPttAsync(_ptt.Toggle());
    public Task CancelTalkAsync() => ApplyPttAsync(_ptt.Cancel());

    public async Task SendTextAsync(string? text, CancellationToken ct = default)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || !Connected) return;
        var turn = BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(turn.Token, ct);
        await RunTurnAsync(text, sttMs: 0, turn, linked.Token);
    }

    public async Task TestVoiceAsync(CancellationToken ct = default)
    {
        var profile = CurrentProfile;
        var label = $"{profile.Provider} · {profile.Voice.Replace(".wav", "").Replace("_", " ")}";
        Append(TranscriptKind.System, "", $"voice test — {label}", showRemember: false);
        StatusText = "Speaking…";
        SetActivity("Speaking…", label);
        TtsLamp = StageLamp.Green;
        Touch();
        using var busy = WakeBusy();
        try
        {
            await _providers.SpeakAsync(profile, SessionCopy.VoiceTestLine, ct);
            StatusText = "Ready";
            SetActivity("Ready");
        }
        catch (Exception ex)
        {
            StatusText = "Voice test failed";
            LogFailure(ex, "TTS test failed");
            ShowError($"Voice test failed ({profile.Provider}): {ex.Message}");
        }
        finally { TtsLamp = StageLamp.Dim; Touch(); }
    }

    public async Task RememberAsync(TranscriptEntry entry)
    {
        if (!IsAliceLocal) return;
        var text = entry.Text.Length > 1000 ? entry.Text[..1000] : entry.Text;
        entry.RememberEnabled = false;
        entry.RememberLabel = "…";
        Touch();
        try
        {
            var ok = await RememberOnBackendAsync(text);
            entry.RememberLabel = ok ? "♥ saved" : "♥ failed";
            entry.RememberEnabled = !ok;
            AddLog(ok ? "Memory: saved to Alice LOCAL" : "Memory: save failed (see log)");
        }
        catch (Exception ex)
        {
            entry.RememberLabel = "♥ failed";
            entry.RememberEnabled = true;
            Log.Warning(ex, "Remember failed");
        }
        Touch();
    }

    public async Task LoadSoulAsync()
    {
        SoulKind = NoteKind.Dim;
        try
        {
            var doc = await _soul.LoadAsync();
            SoulText = doc.Text;
            SoulStatus = SessionCopy.SoulLoaded(doc);
            SoulKind = doc.CreatedFromTemplate ? NoteKind.Yellow : NoteKind.Dim;
            if (doc.CreatedFromTemplate) AddLog($"Created starter Alice LOCAL SOUL at {doc.Path}");
        }
        catch (Exception ex)
        {
            SoulStatus = $"Load failed: {ex.Message}";
            SoulKind = NoteKind.Red;
        }
        Touch();
    }

    public async Task SaveSoulAsync(string text)
    {
        try
        {
            var result = await _soul.SaveAsync(text);
            if (_hub is not null) await _hub.ReloadAllAsync();
            else await ReloadBackendSoulAsync();
            SoulText = text;
            SoulStatus = SessionCopy.SoulSaved(result);
            SoulKind = NoteKind.Green;
            AddLog($"SOUL saved to {result.Path}" + (result.BackupPath is null ? "" : $", backup {result.BackupPath}"));
        }
        catch (Exception ex)
        {
            SoulStatus = $"Save failed: {ex.Message}";
            SoulKind = NoteKind.Red;
        }
        Touch();
    }

    public void StepBackground(int delta) { Backgrounds.Step(delta); Touch(); }
    public void RandomBackground() { Backgrounds.RandomOther(); Touch(); }

    public void OnMicFrame(float[] pcm)
    {
        var rms = _utterance.Append(pcm);
        lock (_gate) _vuLevel = Math.Max(_vuLevel * 0.6f, rms);
        if (Mode == "wake") _wake.Enqueue(pcm);
    }

    public void OnUiTick()
    {
        lock (_gate) _vuLevel *= 0.85f;
        RefreshWakeMeter();
        var clearError = false;
        lock (_gate)
        {
            if (Error is not null && _clock() >= _errorUntil) { Error = null; clearError = true; }
        }
        if (_conversation is { NeedsPoll: true } && _clock() >= _nextConversationPoll)
        {
            _nextConversationPoll = _clock().AddSeconds(2);
            _ = PollConversationAsync();
        }
        if (_utterance.IsFull && !_capNoted && _ptt.Current != PushToTalkGesture.State.Idle)
        {
            _capNoted = true;
            AddLog("Recording cap (120 s) reached — sending");
            _ptt.Reset();
            _ = FinishUtteranceAsync();
        }
        Notify(MeterChanged);
        if (clearError) Touch();
    }

    public void DismissError()
    {
        Error = null;
        Touch();
    }

    public async ValueTask DisposeAsync()
    {
        CancelTurn();
        _mic.AudioFrameCaptured -= OnMicFrame;
        _voice.EventReceived -= OnVoiceEvent;
        try { await StopMicAsync(); } catch { }
        _wake.Dispose();
    }

    private async Task ApplyPttAsync(PttAction action)
    {
        if (!Connected && action == PttAction.StartRecording) return;
        try
        {
            switch (action)
            {
                case PttAction.StartRecording:
                    CancelTurn();
                    _wake.Pause();
                    _capNoted = false;
                    _utterance.Start();
                    await EnsureMicAsync();
                    TalkButtonText = _ptt.Current == PushToTalkGesture.State.Latched ? "■ Send" : "🎤 Release to send";
                    StatusText = "Listening…";
                    SetActivity("Listening…");
                    break;
                case PttAction.StopAndTranscribe:
                    await FinishUtteranceAsync();
                    break;
                default:
                    if (_ptt.Current == PushToTalkGesture.State.Latched) TalkButtonText = "■ Send";
                    break;
            }
        }
        catch (Exception ex)
        {
            _ptt.Reset();
            _utterance.Cancel();
            await StopMicAsync();
            TalkButtonText = "🎤 Talk";
            LogFailure(ex, "PTT failed");
            ShowError($"Microphone error: {ex.Message}");
            ResumeWakeIfIdle();
        }
        Touch();
    }

    private async Task FinishUtteranceAsync()
    {
        using var busy = WakeBusy();
        var pcm = _utterance.Stop();
        await StopMicAsync();
        TalkButtonText = "🎤 Talk";
        if (pcm is null)
        {
            StatusText = "Too short — hold Talk while you speak";
            SetActivity("Ready");
            Touch();
            return;
        }
        await TranscribeAndReplyAsync(pcm);
    }

    private async Task TranscribeAndReplyAsync(short[] pcm)
    {
        var turn = BeginTurn();
        var ct = turn.Token;
        StatusText = "Transcribing…";
        SetActivity("Transcribing…", $"{pcm.Length / 16000.0:F1}s of audio");
        SttLamp = StageLamp.Yellow;
        Touch();
        var sw = Stopwatch.StartNew();
        SttResult stt;
        try { stt = await _voice.TranscribeAsync(pcm, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            SttLamp = StageLamp.Red;
            StatusText = "Transcription failed";
            SetActivity("Ready");
            LogFailure(ex, "STT failed");
            ShowError($"Speech-to-text failed: {ex.Message}");
            Touch();
            return;
        }
        SttLamp = StageLamp.Green;
        if (string.IsNullOrWhiteSpace(stt.Text))
        {
            StatusText = "Didn't catch that";
            SetActivity("Ready");
            Touch();
            return;
        }
        await RunTurnAsync(stt.Text.Trim(), sw.ElapsedMilliseconds, turn, ct);
    }

    private async Task RunTurnAsync(string text, long sttMs, CancellationTokenSource turn, CancellationToken ct)
    {
        using var busy = WakeBusy();
        var myTurn = _turnId;
        var target = TargetName;
        var total = Stopwatch.StartNew();
        var sharedLocal = IsAliceLocal && _conversation is not null;
        OperationTrail.Leave("turn.start", target);
        var userLine = Append(TranscriptKind.User, "You", $"{text} → {target}", showRemember: false);
        if (sharedLocal) userLine.PendingShared = true;
        StatusText = $"Talking to {target}…";
        SetActivity("Thinking…", text);
        LlmLamp = StageLamp.Yellow;
        Touch();

        string reply;
        long ttft = -1;
        var llm = Stopwatch.StartNew();
        try
        {
            if (IsAliceLocal)
            {
                _streamTurnId = myTurn;
                _streamEntry = null;
                void OnFirst(BackendEvent ev) { if (ev.Type == "reply_partial" && ttft < 0) ttft = llm.ElapsedMilliseconds; }
                _voice.EventReceived += OnFirst;
                try { reply = await _voice.AskAsync(text, ReplyStyle.Build(Mood, Profile), ct); }
                finally { _voice.EventReceived -= OnFirst; }
            }
            else
            {
                var member = SelectedMember ?? throw new InvalidOperationException("No fleet member selected");
                reply = await _chat.SendAsync(member, text, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            OperationTrail.Leave("turn.cancel", target);
            AddLog("Turn cancelled");
            return;
        }
        catch (Exception ex)
        {
            if (myTurn == _turnId) { _streamTurnId = 0; _streamEntry = null; }
            LlmLamp = StageLamp.Red;
            // Record the ask failure before any paint. The 18:15 crash threw
            // while drawing the error, so this log never ran and the UI
            // exception replaced the original one.
            OperationTrail.Leave("turn.failed", target + " " + ex.GetType().Name);
            LogFailure(ex, "Turn to {Target} failed", target);
            Append(TranscriptKind.System, "", $"{target}: (no reply — {ex.Message})", showRemember: false);
            StatusText = "Chat failed";
            SetActivity("Ready");
            ShowError($"{target}: {ex.Message}");
            return;
        }
        llm.Stop();
        if (myTurn != _turnId) return;

        if (sharedLocal)
        {
            if (_conversation!.NeedsPoll)
            {
                try { await _conversation.RefreshAsync(ct); }
                catch (Exception ex) { Log.Warning(ex, "Conversation refresh failed"); }
            }
            _streamTurnId = 0;
            _streamEntry = null;
            ApplySharedSnapshot();
        }
        else
        {
            if (_streamEntry is not null)
            {
                _streamEntry.Text = reply;
                _streamEntry.ShowRemember = true;
            }
            else Append(TranscriptKind.Alice, target, reply, showRemember: true);
            _streamTurnId = 0;
            _streamEntry = null;
        }
        LlmLamp = StageLamp.Green;

        long ttsMs = 0;
        if (!string.IsNullOrWhiteSpace(reply))
        {
            StatusText = "Speaking…";
            SetActivity("Speaking…", target);
            TtsLamp = StageLamp.Green;
            Touch();
            var tts = Stopwatch.StartNew();
            try { await _providers.SpeakAsync(CurrentProfile, reply, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogFailure(ex, "TTS failed");
                ShowError($"Speech output failed: {ex.Message}");
            }
            ttsMs = tts.ElapsedMilliseconds;
            TtsLamp = StageLamp.Dim;
        }
        if (myTurn != _turnId) return;

        Latency.Add(new TurnTiming(target, sttMs, llm.ElapsedMilliseconds, ttft < 0 ? llm.ElapsedMilliseconds : ttft, ttsMs, sttMs + total.ElapsedMilliseconds));
        StatusText = $"Replied from {target}";
        OperationTrail.Leave("turn.end", target);
        SetActivity("Ready", target);
        RefreshStatus();
        Touch();
    }

    /// <summary>Tool names are short identifiers. Anything else is omitted so a breadcrumb cannot carry arguments.</summary>
    private static string ToolCrumb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return "unlisted";
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.')) return "unlisted";
        }
        return text;
    }

    private void OnVoiceEvent(BackendEvent evt)
    {
        switch (evt.Type)
        {
            case "reply_partial":
                if (_streamTurnId != 0 && _streamTurnId == _turnId && evt.Text is { Length: > 0 } partial)
                {
                    if (_streamEntry is null)
                    {
                        _streamEntry = Append(TranscriptKind.Alice, TargetName, partial, showRemember: false);
                        if (_conversation is not null && IsAliceLocal) _streamEntry.PendingShared = true;
                    }
                    else _streamEntry.Text = partial;
                    LlmLamp = StageLamp.Green;
                    Touch();
                }
                break;
            case "tool_call":
                var tool = ToolCrumb(evt.Text);
                AddLog(tool == "unlisted" ? "Tool" : "Tool: " + tool);
                OperationTrail.Leave("ui.tool", tool);
                SetActivity("Thinking…", tool == "unlisted" ? "using a tool" : "using " + tool);
                Touch();
                break;
            case "backend_ready":
            case "backend_switched":
            case "reset_ack":
            case "model_info":
                if (evt.Text is not null) AddLog(evt.Text);
                Touch();
                break;
            case "error":
                StageError = evt.Text ?? "";
                ShowError(evt.Text ?? "Error");
                break;
        }
    }

    private async Task<bool> RememberOnBackendAsync(string text, CancellationToken ct = default)
    {
        var backend = _voice.Get(VoiceBackendKind.AliceLocal);
        if (backend is AliceLocalBackend local) return await local.RememberAsync(text, ct);
        if (backend is IAliceExtras extras) return await extras.RememberAsync(text, ct);
        return false;
    }

    private async Task ReloadBackendSoulAsync(CancellationToken ct = default)
    {
        var backend = _voice.Get(VoiceBackendKind.AliceLocal);
        if (backend is AliceLocalBackend local) await local.ReloadSoulAsync(ct);
        else if (backend is IAliceExtras extras) await extras.ReloadSoulAsync(ct);
    }

    private VoiceProfile CurrentProfile => new(SelectedProvider ?? VoiceProviderKind.Sapi, SelectedVoice);

    private async Task RestoreVoiceAsync()
    {
        _memberVoices.TryGetValue(SelectedTargetId, out var saved);
        var available = saved is not null && _providers.Providers.Any(p => p.Kind == saved.Provider);
        if (!available)
        {
            if (saved is not null)
                AddLog($"Voice provider {saved.Provider} is not on this device; using the on-device default.");
            var fallback = _providers.Providers.FirstOrDefault(p => p.Kind == VoiceProviderKind.Sapi)?.Kind
                           ?? _providers.Providers.FirstOrDefault()?.Kind;
            if (fallback is { } kind) await SetProviderQuietAsync(kind, null);
            return;
        }
        _restoring = true;
        try { await SetProviderQuietAsync(saved!.Provider, saved.Voice); }
        finally { _restoring = false; }
    }

    private async Task PollConversationAsync()
    {
        if (_conversation is null) return;
        try { await _conversation.RefreshAsync(); }
        catch (Exception ex) { Log.Debug(ex, "Conversation refresh failed"); }
    }

    /// <summary>Replace Alice LOCAL bubbles with the canonical transcript. Other devices' turns show up here.</summary>
    private void ApplySharedSnapshot()
    {
        if (_conversation is null) return;
        var turns = _conversation.Snapshot();
        lock (_gate)
        {
            _transcript.RemoveAll(e => e.SharedId is not null || e.PendingShared);
            foreach (var turn in turns)
            {
                _turnSeq++;
                var n = _turnSeq;
                _transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.User,
                    Speaker = "You",
                    Text = $"{turn.User} → Alice LOCAL",
                    Turn = n,
                    At = turn.At,
                    SharedId = turn.Id
                });
                _transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Alice,
                    Speaker = "Alice LOCAL",
                    Text = turn.Assistant,
                    Turn = n,
                    At = turn.At,
                    ShowRemember = true,
                    RememberEnabled = true,
                    SharedId = turn.Id
                });
            }
        }
        Touch();
    }

    private async Task SetProviderQuietAsync(VoiceProviderKind kind, string? voice)
    {
        SelectedProvider = kind;
        await LoadVoicesAsync(voice);
    }

    private async Task LoadVoicesAsync(string? select)
    {
        try
        {
            if (SelectedProvider is not { } kind) return;
            var provider = _providers.Providers.FirstOrDefault(p => p.Kind == kind);
            if (provider is null) return;
            var voices = await provider.ListVoicesAsync();
            VoiceNames = voices.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var preferred = select is not null && voices.Contains(select) ? select
                : voices.FirstOrDefault(v => v.Contains("female_06")) ?? voices.FirstOrDefault() ?? "";
            SelectedVoice = preferred;
            AddLog($"Voices loaded: {voices.Count} ({provider.DisplayName})");
        }
        catch (Exception ex) { AddLog($"Voices: using defaults ({ex.Message})"); }
        RefreshStatus();
    }

    private void RememberVoice()
    {
        if (SelectedProvider is not { } kind) return;
        _memberVoices[SelectedTargetId] = new VoiceProfile(kind, SelectedVoice);
        SaveVoices();
    }

    private void SaveVoices()
    {
        try { _assignments.Save(_memberVoices); }
        catch (Exception ex) { Log.Warning(ex, "Failed to save voice assignments"); }
    }

    private async Task WarmUpSttAsync()
    {
        try
        {
            SttLamp = StageLamp.Wait;
            await _voice.Stt.WarmUpAsync();
            SttLamp = StageLamp.Dim;
            AddLog($"STT ready: {_voice.Stt.Status}");
        }
        catch (Exception ex)
        {
            SttLamp = StageLamp.Red;
            AddLog($"STT unavailable: {ex.Message}");
            Log.Warning(ex, "STT warm-up failed");
        }
        RefreshStatus();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _updater.CheckForUpdateAsync();
            if (release is null) return;
            var offer = UpdateOffer.For(release, _host.Kind);
            UpdateBanner = offer.Banner;
            StatusText = offer.Banner.StartsWith("Update available:", StringComparison.Ordinal)
                ? $"Update available: {release.Version}"
                : StatusText;
            Log.Information("Update {Version} available", release.Version);
        }
        catch (Exception ex) { Log.Warning(ex, "Background update check failed"); }
    }

    private async Task StartWakeAsync()
    {
        if (!_wake.IsAvailable) return;
        try { await EnsureMicAsync(); }
        catch (Exception ex)
        {
            LogFailure(ex, "Wake mode could not open the microphone");
            ShowError($"Microphone error: {ex.Message}");
            Mode = "ptt";
            Touch();
            return;
        }
        ResumeWakeIfIdle();
        if (_wakeBusy == 0)
        {
            StatusText = $"Say “{_wake.Phrase}”…";
            SetActivity("Ready", $"listening for “{_wake.Phrase}”");
        }
        AddLog($"Wake listening: {_wake.ModelId}");
        Touch();
    }

    private async Task StopWakeAsync()
    {
        _wake.Pause();
        if (_ptt.Current == PushToTalkGesture.State.Idle) await StopMicAsync();
        Touch();
    }

    private async Task EnsureMicAsync()
    {
        if (!_micRunning)
        {
            await _mic.StartAsync();
            _micRunning = true;
        }
        MicLamp = StageLamp.Green;
        MicTag = "🎤 On";
        MicTagLamp = StageLamp.Green;
    }

    private async Task StopMicAsync()
    {
        if (Mode == "wake" && _wake.IsAvailable)
        {
            MicTag = "🎤 Wake";
            return;
        }
        lock (_gate) _vuLevel = 0;
        if (_micRunning)
        {
            _micRunning = false;
            try { await _mic.StopAsync(); } catch (Exception ex) { Log.Warning(ex, "Mic stop failed"); }
        }
        MicLamp = StageLamp.Dim;
        MicTag = "🎤 Off";
        MicTagLamp = StageLamp.Red;
    }

    private void HookWake(IWakeController wake)
    {
        wake.WakeDetected += score =>
        {
            if (Mode != "wake") return;
            AddLog($"Wake word {wake.ModelId} ({score:0.00})");
            StatusText = $"Heard “{wake.Phrase}” — go ahead…";
            SetActivity("Listening…", $"wake word {score:0.00}");
            MicLamp = StageLamp.Green;
            Touch();
        };
        wake.UtteranceReady += pcm => { _ = OnWakeUtteranceAsync(pcm); };
        wake.UtteranceAbandoned += () =>
        {
            if (Mode != "wake") return;
            StatusText = $"Didn't hear a request — say “{wake.Phrase}” again";
            SetActivity("Ready", $"listening for “{wake.Phrase}”");
            Touch();
        };
        wake.Faulted += ex =>
        {
            Log.Error(ex, "Wake-word inference failed");
            ShowError($"Wake word stopped: {ex.Message}");
            if (Mode == "wake") SetMode("ptt");
        };
    }

    private async Task OnWakeUtteranceAsync(short[] pcm)
    {
        using var busy = WakeBusy();
        if (Mode != "wake") return;
        AddLog($"Wake utterance {pcm.Length / 16000.0:F1}s");
        await TranscribeAndReplyAsync(pcm);
    }

    private IDisposable WakeBusy()
    {
        _wakeBusy++;
        _wake.Pause();
        return new Busy(this);
    }

    private void ResumeWakeIfIdle()
    {
        if (!_wake.IsAvailable || Mode != "wake" || !_micRunning || _wakeBusy > 0) return;
        if (_ptt.Current != PushToTalkGesture.State.Idle) return;
        if (_wake.State != WakeWordSessionState.Paused) return;
        _wake.Resume();
        MicTag = "🎤 Wake";
    }

    private void RefreshWakeMeter()
    {
        if (Mode != "wake" || !_wake.IsAvailable) { WakeScoreText = ""; return; }
        WakeScoreText = _wake.State switch
        {
            WakeWordSessionState.Listening => $"score {_wake.LastScore:0.00}",
            WakeWordSessionState.Capturing => "recording…",
            _ => "paused",
        };
    }

    private CancellationTokenSource BeginTurn()
    {
        CancelTurn();
        _turnCts = new CancellationTokenSource();
        _turnId++;
        return _turnCts;
    }

    private void CancelTurn()
    {
        var cts = _turnCts;
        _turnCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        try { _stopSpeech?.Invoke(); } catch { }
        cts.Dispose();
        _streamTurnId = 0;
        _streamEntry = null;
    }

    private void ApplySensitivity(double value, bool writeEngine)
    {
        Sensitivity = Math.Clamp(value, 0.1, 0.9);
        var threshold = WakeWordSetup.ThresholdFor(Sensitivity);
        if (writeEngine && _wake.IsAvailable) _wake.SetThreshold(threshold);
        SensitivityText = $"{Sensitivity:0.00} (threshold {threshold:0.00})";
        Touch();
    }

    private void SetConnected(bool connected)
    {
        Connected = connected;
        ApiLamp = connected ? StageLamp.Green : StageLamp.Dim;
        WsDisplay = connected ? "api·on" : "api·off";
        RefreshStatus();
    }

    private void SetActivity(string state, string? detail = null)
    {
        Activity = state;
        ActivityDetail = detail ?? "";
    }

    private void RefreshStatus()
    {
        AgentLabel = TargetName;
        BackendLabel = IsAliceLocal ? "local (Ollama)" : "fleet (Hermes API)";
        ProviderLabel = SelectedProvider?.ToString() ?? "—";
        VoiceLabel = string.IsNullOrEmpty(SelectedVoice) ? "—" : SelectedVoice.Replace(".wav", "").Replace("_", " ");
        ConnectionLabel = Connected ? "connected" : "offline";
        var model = "—";
        try { model = IsAliceLocal ? _voice.Get(VoiceBackendKind.AliceLocal).Settings.Model : SelectedMember?.Profile ?? "—"; }
        catch { /* backend not registered in a bare test */ }
        LlmMode = IsAliceLocal ? "alice_local" : SelectedMember?.Name ?? "—";
        SettingsLine = $"Brain: {(IsAliceLocal ? "Ollama" : "Hermes")} · {model} · STT: {_voice.Stt.Status} · TTS: {SelectedProvider?.ToString() ?? "—"} · Voice: {VoiceLabel} · Version: {_updater.CurrentVersion}";
        FooterAuth = IsAliceLocal
            ? "Local session (no API key needed)"
            : _chat.HasApiKey ? "API key loaded" : "No API_SERVER_KEY found";
    }

    private TranscriptEntry Append(TranscriptKind kind, string speaker, string text, bool showRemember)
    {
        if (kind == TranscriptKind.User) _turnSeq++;
        var entry = new TranscriptEntry
        {
            Kind = kind,
            Speaker = kind == TranscriptKind.User ? "You" : speaker,
            Text = text,
            Turn = _turnSeq,
            At = new DateTimeOffset(_local()),
            ShowRemember = showRemember && kind == TranscriptKind.Alice,
            RememberEnabled = IsAliceLocal,
            RememberLabel = "♥ remember"
        };
        lock (_gate) _transcript.Add(entry);
        return entry;
    }

    private void AddLog(string text)
    {
        var line = $"{_local():HH:mm:ss}  {text}";
        lock (_gate)
        {
            _logs.Add(line);
            while (_logs.Count > 500) _logs.RemoveAt(0);
        }
    }

    private void ShowError(string text)
    {
        Error = text;
        _errorUntil = _clock().AddSeconds(12);
        AddLog("Error: " + text);
        Touch();
    }

    /// <summary>
    /// Serilog first, then the screen. A painter that throws must not replace
    /// <see cref="Error"/> or escape onto the UI dispatcher.
    /// </summary>
    private static void LogFailure(Exception ex, string template, params object?[] args)
    {
        try { Log.Error(ex, template, args); }
        catch { /* the in-memory error is the user-facing record */ }
    }

    private void Touch() => Notify(Changed);

    private void Notify(Action? handler)
    {
        if (handler is null) return;
        Delegate[] subscribers;
        try { subscribers = handler.GetInvocationList(); }
        catch (Exception ex)
        {
            NoteNotifyFailure(ex);
            return;
        }
        foreach (var subscriber in subscribers)
        {
            try { ((Action)subscriber)(); }
            catch (Exception ex) { NoteNotifyFailure(ex); }
        }
    }

    private static void NoteNotifyFailure(Exception ex)
    {
        OperationTrail.Leave("ui.notify.failed", ex.GetType().Name);
        try { Log.Error(ex, "UI notification failed"); }
        catch { /* do not let logging replace the session error */ }
    }

    private static bool SessionCopyIsMood(string mood) => mood is "natural" or "comfort" or "playful" or "flirty" or "focused";

    private sealed class Busy : IDisposable
    {
        private readonly VoiceSession _session;
        private bool _done;
        public Busy(VoiceSession session) => _session = session;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _session._wakeBusy = Math.Max(0, _session._wakeBusy - 1);
            _session.ResumeWakeIfIdle();
        }
    }
}
