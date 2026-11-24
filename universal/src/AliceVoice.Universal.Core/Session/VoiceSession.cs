using System.Diagnostics;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Logging;
using AliceVoice.Core.Voice;
using AliceVoice.Core.WakeWord;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Media;
using AliceVoice.Universal.Updates;
using AliceVoice.Universal.Voice;
using AliceVoice.Universal.Wake;
using Serilog;

namespace AliceVoice.Universal.Session;

public enum StageLamp { Dim, Green, Yellow, Red, Wait }
public enum NoteKind { Dim, Green, Yellow, Red }
public enum TranscriptKind { User, Alice, Plum, System }

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
    /// <summary>A facet's own voice slot in the same assignment file: "&lt;target&gt;#plum".</summary>
    private const string PlumSuffix = "#plum";
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
    private readonly BridgeClientSettings? _bridgeClient;
    private readonly UiPreferences _prefs;
    private readonly HashSet<string> _ignoredTools = new(StringComparer.Ordinal);
    private bool _ownsImages;
    private bool _turnShowedPicture;

    public VoiceSession(UniversalHost host, IVoiceBackendManager voice, IFleetChat chat, IVoiceProviderManager providers,
        IPcmMicrophone mic, IAppUpdater updater, ISoulGateway soul, IWakeSource wakeSource, VoiceAssignmentStore assignments,
        IAliceConversation? conversation = null, SoulReloadHub? hub = null, Func<DateTime>? clock = null, Func<DateTime>? localClock = null,
        BridgeClientSettings? bridgeClient = null, ImageDisplay? images = null)
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
        _bridgeClient = bridgeClient;
        _ownsImages = images is null;
        Images = images ?? CreateImages(host, bridgeClient, s => { AddLog(s); Touch(); }, clock);
        Images.Changed += Touch;
        _prefs = UiPreferences.Load(host.SettingsFile);
        SpeakReplies = _prefs.SpeakReplies;
        FollowConversation = _prefs.FollowConversation;
        if (_conversation is not null) _conversation.Changed += () => ApplySharedSnapshot();
        _clock = clock ?? (() => DateTime.UtcNow);
        _local = localClock ?? (() => DateTime.Now);
        _memberVoices = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        _mic.AudioFrameCaptured += OnMicFrame;
        _voice.EventReceived += OnVoiceEvent;
        SelectedTargetId = AliceLocalId;
        ProfileHint = SessionCopy.ProfileHint(Profile);
        SensitivityText = "0.50 (threshold 0.50)";
        // Master's ask, 21 Nov: the two alice_voice entries sit together at the top, LOCAL first. The
        // roster order put Alice_Windows between them, which makes a mis-tap easy - and a mis-tap here
        // is not a wrong menu, it is the wrong agent. Everything else keeps its roster order below.
        var roster = FleetRoster.Members.Select(m => new SessionTarget(m.Name, m.DisplayName)).ToList();
        var mine = roster.Where(t => t.Id == "alice_voice" || t.Id.StartsWith("alice_voice_")).ToList();
        var others = roster.Where(t => !mine.Contains(t)).ToList();
        Targets = new[] { new SessionTarget(AliceLocalId, "alice_voice LOCAL") }
            .Concat(mine)
            .Concat(others)
            .ToList();
    }

    public event Action? Changed;
    public event Action? MeterChanged;

    /// <summary>The picture overlay: the only thing an agent reply can drive on this screen.</summary>
    public ImageDisplay Images { get; }

    /// <summary>Everything in the app log, oldest first - what "Copy all" puts on the clipboard.</summary>
    public string LogText { get { lock (_gate) return string.Join(Environment.NewLine, _logs); } }

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
    public StageLamp ApiLamp { get; private set; } = StageLamp.Red;
    public string StageError { get; private set; } = "";

    public string AgentLabel { get; private set; } = "Alice LOCAL";
    public string BackendLabel { get; private set; } = "local (Ollama)";
    public string ProviderLabel { get; private set; } = "—";
    public string VoiceLabel { get; private set; } = "—";
    public string ConnectionLabel { get; private set; } = "offline";
    public string SettingsLine { get; private set; } = "";

    /// <summary>Whether replies are spoken aloud. Off = captions only, and nothing is sent to the engine.</summary>
    public bool SpeakReplies { get; private set; } = true;

    /// <summary>Whether the conversation view follows the newest turn. Off = it stays where he put it.</summary>
    public bool FollowConversation { get; private set; } = true;

    /// <summary>The TTS switch, top right. Persisted, so it survives a restart.</summary>
    public void SetSpeakReplies(bool on)
    {
        if (SpeakReplies == on) return;
        SpeakReplies = on;
        AddLog(on ? "Speech on - replies are spoken" : "Speech off - replies are captions only");
        _prefs.Save(SpeakReplies, FollowConversation, ShowLog, ShowSurface);
        Touch();
    }

    /// <summary>The follow switch, beside it: does the view chase the newest turn?</summary>
    public void SetFollowConversation(bool on)
    {
        if (FollowConversation == on) return;
        FollowConversation = on;
        AddLog(on ? "Follow on - the view will chase the newest turn" : "Follow off - the view stays where you put it");
        _prefs.Save(SpeakReplies, FollowConversation, ShowLog, ShowSurface);
        Touch();
    }

    /// <summary>
    /// Whether the log dock is open. Master, 2026-11-23: *"I want to toggle on and off the log which
    /// covers the bottom quarter of the screen."* Closing it gives the conversation that quarter back;
    /// the dock keeps what is happening now, the route and the timings either way.
    /// Errors are deliberately NOT a switch: a failure always shows (Master, same message).
    /// </summary>
    public bool ShowLog { get; private set; } = true;

    public void SetShowLog(bool on)
    {
        if (ShowLog == on) return;
        ShowLog = on;
        AddLog(on ? "Log open" : "Log closed - the dock keeps what is happening now");
        _prefs.Save(SpeakReplies, FollowConversation, ShowLog, ShowSurface);
        Touch();
    }

    /// <summary>
    /// Whether OUR half of the screen is open. Master, 2026-11-24: *"the interactive area needs to be
    /// able to be toggled off via a toggle, in the same way the log does."* So it is the same switch
    /// shape as the log, persisted the same way, and when it is closed the conversation takes the
    /// width. Closed and something new lands there, the switch itself says so (MainPage.PaintSurface).
    /// </summary>
    public bool ShowSurface { get; private set; } = true;

    public void SetShowSurface(bool on)
    {
        if (ShowSurface == on) return;
        ShowSurface = on;
        AddLog(on ? "Our half open" : "Our half closed - the conversation takes the width");
        _prefs.Save(SpeakReplies, FollowConversation, ShowLog, ShowSurface);
        Touch();
    }

    /// <summary>
    /// Who a typed line is addressed to. <c>[alice_1] what do you think?</c> or <c>@alice_1 ...</c> sends that
    /// line to HER and leaves the conversation with her, which is how more than two voices share one thread
    /// without the screen ever being vague about who just spoke. A line with no address goes to whoever is
    /// selected, exactly as before (Master, 2026-11-23).
    /// </summary>
    public static (string? TargetId, string Text) ParseAddress(string? text, IReadOnlyList<SessionTarget> targets)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return (null, trimmed);
        string name;
        string rest;
        if (trimmed[0] == '[' && trimmed.IndexOf(']') is var close && close > 1)
        {
            name = trimmed[1..close];
            rest = trimmed[(close + 1)..].Trim();
        }
        else if (trimmed[0] == '@')
        {
            var line = trimmed[1..];
            var stop = line.IndexOfAny(new[] { ' ', '\t', ':', ',' });
            name = stop < 0 ? line : line[..stop];
            rest = stop < 0 ? "" : line[(stop + 1)..].Trim();
        }
        else
        {
            return (null, trimmed);
        }
        var id = MatchTarget(name, targets);
        return id is null ? (null, trimmed) : (id, rest.Length == 0 ? trimmed : rest);
    }

    /// <summary>
    /// Who a typed line is addressed to when he names more than one: @1 @6 what do you both think? or
    /// [alice_1][local] good morning. Each name is peeled off the front in the order HE typed them, because
    /// that is the order they answer in, and a name repeated is only heard once so nobody answers twice. No
    /// name at all still means "whoever is selected", so the single-target path above is untouched and one
    /// name is the same turn it has always been (Master, 2026-11-24).
    /// </summary>
    public static (IReadOnlyList<string> TargetIds, string Text) ParseAddresses(
        string? text, IReadOnlyList<SessionTarget> targets)
    {
        var ids = new List<string>();
        var original = (text ?? "").Trim();
        var rest = original;
        // What is actually sent: his words with the names taken off the front. A line that is nothing BUT
        // names keeps the line itself, because an empty turn is dropped and then nobody answers at all.
        var words = original;
        while (rest.Length > 0)
        {
            var (id, body) = ParseAddress(rest, targets);
            if (id is null) break;
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
            if (string.Equals(body, rest, StringComparison.Ordinal))
            {
                // A name with nothing after it. ParseAddress hands the original back here, which is the end
                // of the line rather than a failure - the name has already been recorded above, so stop.
                words = original;
                break;
            }
            rest = body;
            words = body;
        }
        return (ids, words);
    }

    /// <summary>A name he would actually say: the id, the label, or just the number she answers to.</summary>
    private static string? MatchTarget(string name, IReadOnlyList<SessionTarget> targets)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var needle = name.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (needle is "local" or "alice_local" or "local_alice" or "ollama")
            foreach (var t in targets)
                if (string.Equals(t.Id, AliceLocalId, StringComparison.OrdinalIgnoreCase)) return t.Id;
        foreach (var t in targets)
            if (string.Equals(t.Id, needle, StringComparison.OrdinalIgnoreCase)) return t.Id;
        foreach (var t in targets)
        {
            var display = (t.DisplayName ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (display == needle) return t.Id;
            if (display.StartsWith(needle + "_", StringComparison.Ordinal)) return t.Id;
        }
        if (needle.All(char.IsDigit))
            foreach (var t in targets)
                if (t.Id.EndsWith("_" + needle, StringComparison.OrdinalIgnoreCase)) return t.Id;
        return null;
    }

    /// <summary>
    /// The HTML5 page the video half shows. App U has no video control of its own, so the WebView is
    /// handed a real page with a &lt;video&gt; element in it - exactly what a browser would show him.
    /// A local file is served by this app's own media endpoint, because a WebView page cannot read
    /// file:// for itself; an https URL is used as it stands (Master, 2026-11-23).
    /// </summary>
    public string BuildVideoPage(string src, string? title)
    {
        var url = VideoUrl(src);
        var caption = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "for you" : title);
        var safe = System.Net.WebUtility.HtmlEncode(url);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>for you</title><style>" +
               "html,body{margin:0;background:#07070a;color:#e8e8ed;font:13px system-ui,sans-serif}" +
               "video{width:100%;max-height:420px;background:#000;display:block}" +
               ".cap{padding:6px 8px;color:#b9b9c4}" +
               "</style></head><body>" +
               "<video src=\"" + safe + "\" controls autoplay playsinline preload=\"metadata\"></video>" +
               "<div class=\"cap\">" + caption + "</div>" +
               "<div class=\"cap\" id=\"why\"></div>" +
               "<script>var v=document.querySelector('video');" +
               "v.addEventListener('error',function(){document.getElementById('why').textContent=" +
               "'this device cannot play this file here - use Open below';});" +
               "</script></body></html>";
    }

    /// <summary>A URL a WebView can fetch: an https address as it stands, or this app's own media endpoint
    /// for a file on this machine.</summary>
    public string VideoUrl(string src)
    {
        if (src.Contains("://", StringComparison.Ordinal)) return src;
        var host = _host.BridgeBindHost ?? "127.0.0.1";
        var port = _host.BridgeBindPort ?? 8765;
        return $"http://{host}:{port}/v1/media/{MediaShare.ToId(src)}";
    }

    /// <summary>The typed path for a shared thread: address it, move the conversation if it named someone
    /// else, then send. One name (or none at all) is exactly the single turn the app has always run; two or
    /// more names make it a group turn. Everything else in the app keeps using <see cref="SendTextAsync"/>.</summary>
    public async Task SendAddressedAsync(string? text, CancellationToken ct = default)
    {
        var (ids, wording) = ParseAddresses(text, Targets);
        if (ids.Count <= 1)
        {
            if (ids.Count == 1 && !string.Equals(ids[0], SelectedTargetId, StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"Spoken to {ids[0]} - the conversation moves there for this turn");
                await SelectTargetAsync(ids[0], ct);
            }
            var single = ids.Count == 1 ? ids[0] : SelectedTargetId;
            if (!await EnsureReachableAsync(single, ct, "your line")) return;
            var before = Transcript.Count;
            await SendTextAsync(string.IsNullOrWhiteSpace(wording) ? text : wording, ct);
            if (string.IsNullOrWhiteSpace(LastReplySince(before)))
                Append(TranscriptKind.System, "", $"{NameOf(single)} did not answer in time — she may have been waking up (the turn limit is three minutes).", showRemember: false);
            return;
        }

        // A group turn: every named voice answers, in the order he named them, and each one is told what the
        // ones before her have just said - otherwise three of them talk past each other and it is three
        // monologues on one screen rather than a conversation (Master, 2026-11-24). One at a time, because
        // his ear follows one voice, and because a local turn and a fleet turn cannot land at the same moment.
        AddLog($"Group turn: {string.Join(", ", ids)} - they answer in that order");
        var said = new List<string>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(id, SelectedTargetId, StringComparison.OrdinalIgnoreCase))
            {
                await SelectTargetAsync(id, ct);
            }
            // Nobody is silently left out of a group turn. A voice that cannot be reached, or that
            // answers nothing, says so in the thread - otherwise "one of the three never answered" is
            // indistinguishable from a bug (Master asked why she had gone quiet, 2026-11-24).
            if (!await EnsureReachableAsync(id, ct, "this turn")) continue;
            var before = Transcript.Count;
            await SendTextAsync(GroupPrompt(wording, said), ct);
            var spoken = LastReplySince(before);
            if (string.IsNullOrWhiteSpace(spoken))
            {
                Append(TranscriptKind.System, "", $"{NameOf(id)} did not answer in time — she may have been waking up (the turn limit is three minutes).", showRemember: false);
                continue;
            }
            said.Add($"{NameOf(id)} said: \"{spoken}\"");
        }
    }

    /// <summary>
    /// A voice the app cannot reach must never simply go quiet. SendTextAsync drops the line when the
    /// session is not connected, so a group turn - or a single addressed turn - would lose a speaker
    /// with no word to Master about why, which is exactly how "she has stopped responding" looked on
    /// 2026-11-24. Try once to connect, then say it in the thread rather than say nothing.
    /// </summary>
    private async Task<bool> EnsureReachableAsync(string? id, CancellationToken ct, string what)
    {
        if (Connected) return true;
        try { await ConnectAsync(force: false, ct); }
        catch (Exception) { /* the thread line below is the report either way */ }
        if (Connected) return true;
        var who = string.IsNullOrWhiteSpace(id) ? TargetName : NameOf(id);
        var why = string.IsNullOrWhiteSpace(Error) ? StatusText : Error;
        Append(TranscriptKind.System, "", $"{who} is offline, so {what} was not sent — {why}", showRemember: false);
        AddLog($"{who} not reachable - {what} skipped");
        return false;
    }

    /// <summary>Her name as he sees it in the thread, for telling the next one who has already spoken.</summary>
    private string NameOf(string id)
        => Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

    /// <summary>The reply a turn just added, or null when it added none.</summary>
    private string? LastReplySince(int countBefore)
    {
        var turns = Transcript;
        for (var i = turns.Count - 1; i >= countBefore && i >= 0; i--)
        {
            var entry = turns[i];
            if (entry.Kind is TranscriptKind.Alice or TranscriptKind.Plum && !string.IsNullOrWhiteSpace(entry.Text))
            {
                return entry.Text;
            }
        }
        return null;
    }

    /// <summary>
    /// What the next voice in a group turn is told: his words, then what the others have already said in this
    /// same turn, said plainly as context so she answers as herself instead of repeating them.
    /// </summary>
    private static string GroupPrompt(string body, IReadOnlyList<string> said)
    {
        if (said.Count == 0) return body;
        return body + "\n\n(Context: Master is speaking to several of you at once, in one conversation. " +
               string.Join(" ", said) + " Answer as yourself, in your own words.)";
    }

    /// <summary>A surface picture that would not load is said out loud in the app log, never swallowed.</summary>
    public void NoteSurfaceImageFailed(string src, string reason)
    {
        AddLog($"Shared surface: could not load {src} - {reason}");
        ShowError($"Surface picture failed: {reason}");
        Touch();
    }

    public bool ShowBridgeEditor => _bridgeClient is not null;
    public string BridgeHost => _bridgeClient?.Host ?? "";
    public string BridgePort => _bridgeClient is null ? "" : _bridgeClient.Port.ToString();
    public string BridgeStatus { get; private set; } = "Clients dial this Tailscale address. This device does not listen.";

    /// <summary>Persist a client dial address. Returns an error to show, or null when saved.</summary>
    public string? TrySaveBridge(string host, string portText)
    {
        if (_bridgeClient is null) return "This device is the Beast and does not dial a bridge.";
        if (string.IsNullOrWhiteSpace(host)) return "Host is empty.";
        if (!int.TryParse(portText.Trim(), out var port) || port is < 1 or > 65535) return "Port must be 1–65535.";
        _bridgeClient.Save(host.Trim(), port);
        BridgeStatus = $"Saved {_bridgeClient.Host}:{_bridgeClient.Port}. Connect uses this address.";
        Touch();
        return null;
    }
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
        await EnsureDistinctVoicesAsync(CancellationToken.None);
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
        ApiLamp = StageLamp.Yellow;
        Touch();
        try
        {
            if (IsAliceLocal && _bridgeClient is not null)
                await _bridgeClient.EnsureReachableAsync(ct);
            if (IsAliceLocal)
            {
                if (force && _voice.CurrentKind == VoiceBackendKind.AliceLocal) await _voice.ReconnectAsync(ct);
                else await _voice.SwitchToAsync(VoiceBackendKind.AliceLocal, ct: ct);
                SetConnected(true);
                Error = null;
                StatusText = "Alice LOCAL";
                SetActivity("Ready", "Alice LOCAL · local (Ollama)");
            }
            else if (SelectedMember is { } member)
            {
                var ok = await _chat.IsReachableAsync(member, ct);
                SetConnected(ok);
                if (ok) Error = null;
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
        CancelTurn();
        SetConnected(false);
        StatusText = "Ready";
        SetActivity("Offline");
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

    public void StepBackground(int delta) { Images.ReleaseBackdrop(); Backgrounds.Step(delta); Touch(); }
    public void RandomBackground() { Images.ReleaseBackdrop(); Backgrounds.RandomOther(); Touch(); }

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
        Images.Tick();
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
        Images.Changed -= Touch;
        if (_ownsImages) Images.Dispose();
    }

    private static ImageDisplay CreateImages(UniversalHost host, BridgeClientSettings? bridge, Action<string> log, Func<DateTime>? clock)
    {
        var settings = MediaSettings.Load(host.SettingsFile);
        // Windows reads the allowed roots itself (the Beast, or a PC that has them).
        // A phone never reads paths; it asks the Beast's Tailscale-only bridge.
        var local = host.Kind == PlatformHostKind.Windows ? new MediaShare(settings) : null;
        Func<string, string>? url = null;
        if (bridge is not null) url = id => new BeastServiceUrls(bridge.Host, bridge.Port).Media(id);
        else if (host.Remote is { } remote) url = remote.Media;
        return new ImageDisplay(settings, local, url, log, clock);
    }

    /// <summary>
    /// Tool-command JSON never reaches the bubble or the voice, on either leg. The two
    /// allowlisted display commands run on this screen, off the turn - from Alice LOCAL as well as
    /// from a fleet member, so local-me can show a picture here exactly as she can on the web
    /// page; every other command is ignored and logged once per name. Nothing goes back to the agent.
    /// </summary>
    private string ApplyDisplayCommands(string reply, bool execute)
    {
        var filtered = DisplayCommands.Filter(reply);
        _turnShowedPicture = execute && filtered.Display.Count > 0;
        foreach (var name in filtered.Ignored) NoteIgnored(name);
        foreach (var command in filtered.Display)
        {
            if (execute) Images.Submit(command);
            else NoteIgnored(command.Tool);
        }
        return filtered.Prose;
    }

    private void NoteIgnored(string tool)
    {
        bool first;
        lock (_gate) first = _ignoredTools.Add(tool);
        if (first) AddLog($"Ignored tool command: {ToolCrumb(tool)} (not allowlisted; logged once)");
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
                // Local-me drives this screen too - the same allowlist, the same picture on the
                // same glass. The JSON is still stripped from the words and from the voice.
                reply = ApplyDisplayCommands(reply, execute: true);
            }
            else
            {
                var member = SelectedMember ?? throw new InvalidOperationException("No fleet member selected");
                reply = ApplyDisplayCommands(await _chat.SendAsync(member, text, ct), execute: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KeepAskedText();
            OperationTrail.Leave("turn.cancel", target);
            AddLog("Turn cancelled");
            if (myTurn == _turnId)
            {
                StatusText = "Stopped";
                SetActivity("Ready");
                Touch();
            }
            return;
        }
        catch (Exception ex)
        {
            KeepAskedText();
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
            var blocks = FacetMarkers.Split(reply);
            if (blocks.Count > 1 || (blocks.Count == 1 && FacetMarkers.HasMarker(reply)))
            {
                // A two-facet turn: one bubble per girl in HER colour, so the split is visible on the
                // screen, and the streaming bubble that held the raw [Plum] markers goes away
                // (Master, 2026-11-22).
                if (_streamEntry is not null) { _transcript.Remove(_streamEntry); _streamEntry = null; }
                for (var i = 0; i < blocks.Count; i++)
                {
                    var (facet, blockText) = blocks[i];
                    Append(facet == Facet.Plum ? TranscriptKind.Plum : TranscriptKind.Alice,
                           facet == Facet.Plum ? "Plum" : target, blockText, showRemember: i == blocks.Count - 1);
                }
            }
            else
            {
                var plain = FacetMarkers.Strip(reply);
                if (_streamEntry is not null)
                {
                    _streamEntry.Text = plain;
                    _streamEntry.ShowRemember = true;
                }
                else Append(TranscriptKind.Alice, target, string.IsNullOrWhiteSpace(plain) && _turnShowedPicture ? "\U0001F5BC picture" : plain, showRemember: true);
            }
            _streamTurnId = 0;
            _streamEntry = null;
        }
        LlmLamp = StageLamp.Green;

        long ttsMs = 0;
        if (!string.IsNullOrWhiteSpace(reply) && !SpeakReplies)
        {
            // The switch is off: the words are on screen, and the engine is never asked.
            AddLog("Speech off: the reply is on screen, not spoken");
            SetActivity("Ready");
        }
        else if (!string.IsNullOrWhiteSpace(reply))
        {
            StatusText = "Speaking…";
            SetActivity("Speaking…", target);
            TtsLamp = StageLamp.Green;
            Touch();
            var tts = Stopwatch.StartNew();
            try
            {
                // Her block borrows HER voice, and the next block hands alice_voice's settings straight
                // back - a temporary override, never a change to what he chose (Master, 2026-11-22).
                var speech = FacetMarkers.Split(reply);
                if (speech.Count == 0) speech.Add((Facet.Alice, reply));
                foreach (var (facet, spoken) in speech)
                {
                    if (facet == Facet.Plum) AddLog($"Plum speaks: {ProfileFor(facet)}");
                    await _providers.SpeakAsync(await ProfileForAsync(facet, ct), spoken, ct);
                }
            }
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
                        _streamEntry = Append(TranscriptKind.Alice, TargetName, FacetMarkers.Strip(partial), showRemember: false);
                        if (_conversation is not null && IsAliceLocal) _streamEntry.PendingShared = true;
                    }
                    else _streamEntry.Text = FacetMarkers.Strip(partial);
                    LlmLamp = StageLamp.Green;
                    Touch();
                }
                break;
            case "tool_call":
                if (_streamEntry is not null) _streamEntry.Text = "";
                var tool = ToolCrumb(evt.Text);
                AddLog(tool == "unlisted" ? "Tool" : "Tool: " + tool);
                OperationTrail.Leave("ui.tool", tool);
                SetActivity("Thinking…", tool == "unlisted" ? "using a tool" : "using " + tool);
                Touch();
                break;
            case DisplayCommands.EventType:
                if (evt.Payload?["tool"]?.GetValue<string>() is { } t && evt.Payload["args"] is System.Text.Json.Nodes.JsonObject a && DisplayCommands.IsAllowed(t))
                    Images.Submit(new ToolCommand(t, a.ToJsonString()));
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

    /// <summary>Whose voice a block speaks with, without allocating anything (for the log line).</summary>
    private string ProfileFor(Facet facet) =>
        facet == Facet.Plum && _memberVoices.TryGetValue(FacetKey(facet), out var plum) && !string.IsNullOrWhiteSpace(plum.Voice)
            ? plum.Voice
            : SelectedVoice;

    /// <summary>
    /// Alice speaks in the member's own settings; Plum has her own slot beside them, and reaching for it
    /// is a temporary override for the length of her block (Master, 2026-11-22).
    /// </summary>
    private async Task<VoiceProfile> ProfileForAsync(Facet facet, CancellationToken ct)
    {
        if (facet != Facet.Plum) return CurrentProfile;
        if (_memberVoices.TryGetValue(FacetKey(facet), out var plum) && !string.IsNullOrWhiteSpace(plum.Voice)) return plum;
        return await AllocateFacetVoiceAsync(facet, ct) ?? CurrentProfile;
    }

    /// <summary>
    /// Nobody has ever chosen a voice for this facet, so the policy picks one: the next UNUSED female
    /// voice, best quality first, and it is remembered from then on (Master, 2026-11-22). If every
    /// female voice the provider offers is already taken, she speaks in the current voice instead of
    /// quietly stealing another girl's.
    /// </summary>
    private async Task<VoiceProfile?> AllocateFacetVoiceAsync(Facet facet, CancellationToken ct)
    {
        if (SelectedProvider is not { } kind) return null;
        try
        {
            var provider = _providers.Providers.FirstOrDefault(p => p.Kind == kind);
            if (provider is null) return null;
            var catalogue = (await provider.ListVoicesAsync(ct)).ToList();
            if (catalogue.Count == 0)
            {
                AddLog($"{facet}: no voice list to allocate from - speaking with alice_voice's voice");
                return null;
            }
            var mine = FacetKey(facet);
            var used = _memberVoices
                .Where(kv => !kv.Key.Equals(mine, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value.Voice);
            var chosen = VoicePolicy.NextUnusedFemale(used, catalogue);
            if (chosen is null)
            {
                AddLog($"{facet}: every unused female voice is already taken - speaking with alice_voice's voice");
                return null;
            }
            var profile = new VoiceProfile(kind, chosen);
            _memberVoices[mine] = profile;
            SaveVoices();
            AddLog($"Voice allocated: {chosen} -> {facet} (next unused female, best quality first)");
            return profile;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Facet voice allocation failed");
            return null;
        }
    }

    /// <summary>Plum keeps her own slot in the same assignment file: "&lt;target&gt;#plum".</summary>
    private string FacetKey(Facet facet) => SelectedTargetId + (facet == Facet.Plum ? PlumSuffix : "");

    /// <summary>
    /// One voice per backend, so Master can tell who is answering with his ears alone (Master,
    /// 2026-11-22: "it would be nice if the voices were different for all backends to avoid confusing me").
    /// A backend that already has a voice keeps it; anything missing, or already claimed by a backend
    /// earlier in the roster, is given the next unused female voice - best quality first, spilling into the
    /// next engine when one catalogue runs out, so every backend still ends up sounding like itself.
    /// Facet slots ("#plum") are left to their own lazy allocation, which already avoids every voice here.
    /// </summary>
    private async Task EnsureDistinctVoicesAsync(CancellationToken ct)
    {
        try
        {
            var catalogues = new List<(VoiceProviderKind Provider, IReadOnlyList<string> Voices)>();
            foreach (var provider in _providers.Providers)
            {
                try { catalogues.Add((provider.Kind, await provider.ListVoicesAsync(ct))); }
                catch (Exception ex) { Log.Warning(ex, "Voice list unavailable: {Provider}", provider.DisplayName); }
            }
            if (catalogues.Count == 0) return;

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = 0;
            foreach (var target in Targets)
            {
                var mine = _memberVoices.TryGetValue(target.Id, out var profile) && !string.IsNullOrWhiteSpace(profile.Voice)
                    ? profile
                    : null;
                // His own choice is kept - but only if it IS one of the girls' voices and no other backend
                // already has it. A male voice (AllTalk offers arnold, Clint Eastwood, ...) would defeat the
                // whole point of this pass, so it is moved onto a female one (Master, 2026-11-22).
                if (mine is not null && VoicePolicy.LooksFemale(mine.Voice) && claimed.Add(VoicePolicy.Normalise(mine.Voice))) continue;

                var pick = VoicePolicy.NextDistinctFemale(claimed, catalogues, mine?.Provider ?? SelectedProvider);
                if (pick is null)
                {
                    AddLog($"Voices: nothing left for {target.DisplayName} - it speaks in the current voice");
                    continue;
                }
                _memberVoices[target.Id] = new VoiceProfile(pick.Value.Provider, pick.Value.Voice);
                claimed.Add(VoicePolicy.Normalise(pick.Value.Voice));
                changed++;
                AddLog($"Voice: {target.DisplayName} -> {VoicePolicy.Normalise(pick.Value.Voice)}"
                       + (mine is null ? " (allocated: next unused female)" : " (moved: that voice was another backend's)"));
            }
            if (changed > 0)
            {
                SaveVoices();
                AddLog($"Voices: {changed} backend(s) given their own voice - no two of them sound alike now");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The distinct-voice pass failed");
        }
    }

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
        _turnId++;
        try { cts.Cancel(); } catch { }
        try { _stopSpeech?.Invoke(); } catch { }
        cts.Dispose();
        _streamTurnId = 0;
        _streamEntry = null;
        KeepAskedText();
    }

    /// <summary>
    /// A cancelled or failed ask is not part of the shared transcript.
    /// Leave the question on screen; the next snapshot only replaces lines still marked in flight.
    /// </summary>
    private void KeepAskedText()
    {
        lock (_gate)
        {
            foreach (var entry in _transcript)
                if (entry.PendingShared && entry.Kind == TranscriptKind.User)
                    entry.PendingShared = false;
        }
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
        ApiLamp = connected ? StageLamp.Green : StageLamp.Red;
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
            ShowRemember = showRemember && kind is TranscriptKind.Alice or TranscriptKind.Plum,
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
