using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Audio;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using System.Diagnostics;
using System.Text.Json;

namespace AliceVoice.App;

public sealed partial class MainWindow : Window
{
    private readonly AliceVoiceOptions _options;
    private readonly IAppUpdater _updater;
    private readonly IVoiceBackendManager _voice;
    private readonly IMicrophoneCapture _mic;
    private readonly ITtsPlayback _tts;
    private readonly SoulStore _soulStore;
    private readonly IFleetChat _chat;
    private readonly IVoiceProviderManager _voiceProvider;

    // ── Push-to-talk: record while held, transcribe once on release ──
    private readonly PushToTalkGesture _ptt = new();
    private readonly UtteranceBuffer _utterance = new(minSeconds: 0.3, maxSeconds: 120);
    private bool _pointerDrivenClick;
    private bool _micRunning;
    private float _vuLevel;
    private DispatcherQueueTimer? _vuTimer;

    // ── Turn state ──
    private CancellationTokenSource? _turnCts;
    private int _turnId;
    private int _streamTurnId;
    private TextBlock? _streamText;
    private Button? _streamRemember;
    private readonly List<TurnTiming> _timings = new();

    private string _mood = "natural";
    private string _mode = "ptt";
    private string _lastNonSoulMode = "ptt";
    private string _profile = "natural";
    private int _turnSeq;
    private bool _connected;
    private bool _initializing = true;
    private bool _logOpen, _helpOpen, _settingsOpen, _metricsOpen, _tuningOpen;
    private readonly List<string> _backgroundImages = new();
    private int _bgIdx = -1;
    private DispatcherQueueTimer? _errorTimer;

    // Per-fleet-member voice assignment: member name -> { provider, voice }.
    private readonly Dictionary<string, VoiceProfile> _memberVoices = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string VoiceAssignmentsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliceVoice", "voice-assignments.json");

    private sealed record TurnTiming(string Target, long SttMs, long LlmMs, long TtftMs, long TtsMs, long TotalMs);

    public MainWindow()
    {
        _options = App.Services.GetRequiredService<AliceVoiceOptions>();
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(_options.CrashDir);
                File.WriteAllText(Path.Combine(_options.CrashDir, "xaml_crash.txt"), ex.ToString());
            }
            catch { }
            throw;
        }

        _updater = App.Services.GetRequiredService<IAppUpdater>();
        _voice = App.Services.GetRequiredService<IVoiceBackendManager>();
        _mic = App.Services.GetRequiredService<IMicrophoneCapture>();
        _tts = App.Services.GetRequiredService<ITtsPlayback>();
        _chat = App.Services.GetRequiredService<IFleetChat>();
        _voiceProvider = App.Services.GetRequiredService<IVoiceProviderManager>();
        _soulStore = App.Services.GetRequiredService<SoulStore>();

        _voice.EventReceived += OnVoiceEvent;
        _mic.AudioFrameCaptured += OnMicFrame;

        // Hold-to-talk needs press AND release, which Button.Click alone can't give.
        TalkButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(TalkButton_PointerPressed), true);
        TalkButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TalkButton_PointerReleased), true);
        TalkButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(TalkButton_PointerCanceled), true);
        TalkButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(TalkButton_PointerCanceled), true);
        ToolTipService.SetToolTip(TalkButton, "Hold to talk — release to send. Tap to latch on, tap again to send.");

        // Populate the agent selector: Alice LOCAL first, then fleet members.
        // (_initializing suppresses SelectionChanged side effects during setup.)
        FleetSelector.Items.Add(new ComboBoxItem { Content = "Alice LOCAL", Tag = "alice_local" });
        foreach (var m in FleetRoster.Members)
            FleetSelector.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.Name });
        FleetSelector.SelectedIndex = 0;

        foreach (var p in _voiceProvider.Providers)
            VoiceProviderSelect.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Kind });

        // Controls that have no implementation yet are disabled, with a reason.
        ModeWakeBtn.IsEnabled = false;
        ToolTipService.SetToolTip(ModeWakeBtn, "Wake word is not implemented yet — use PTT or Text.");
        foreach (var s in new[] { CtxPointsSlider, CtxHonchoSlider, CtxCharsSlider })
        {
            s.IsEnabled = false;
            ToolTipService.SetToolTip(s, "Not implemented yet — context injection is controlled by the agent.");
        }
        MGapLabel.Text = "TTFT:";
        BrandBadge.Text = $"v{_updater.CurrentVersion} · yours";

        Closed += (_, _) => OnWindowClosed();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800)); } catch { }
        LoadBackgrounds();
        LoadMemberVoices();
        _initializing = false;
        _ = LoadVoicesForSelectedProviderAsync();
        _ = CheckForUpdatesAsync();
        _ = WarmUpSttAsync();
        AppendSystem("Talk to me, Master. Hold 🎤 Talk while you speak.");
        UpdateStatusPanel();
        await ConnectCurrentAsync();
    }

    /// <summary>Load the faster-whisper model in the background so the first utterance is fast.</summary>
    private async Task WarmUpSttAsync()
    {
        try
        {
            PStageStt.Text = "⏳ STT";
            await _voice.Stt.WarmUpAsync();
            PStageStt.Text = "⚫ STT";
            AddLog($"STT ready: {_voice.Stt.Status}");
        }
        catch (Exception ex)
        {
            PStageStt.Text = "✖ STT";
            AddLog($"STT unavailable: {ex.Message}");
            Log.Warning(ex, "STT warm-up failed");
        }
        UpdateStatusPanel();
    }

    // ── Background images ──
    private void LoadBackgrounds()
    {
        try
        {
            var dir = _options.ResolvedBackgroundsDir;
            if (Directory.Exists(dir))
                _backgroundImages.AddRange(Directory.GetFiles(dir, "*.jpg").Concat(Directory.GetFiles(dir, "*.png")));
        }
        catch (Exception ex) { Log.Warning(ex, "Background load failed"); }
        if (_backgroundImages.Count > 0) SetBg(0);
    }

    private void SetBg(int idx)
    {
        if (_backgroundImages.Count == 0) return;
        _bgIdx = ((idx % _backgroundImages.Count) + _backgroundImages.Count) % _backgroundImages.Count;
        try
        {
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_backgroundImages[_bgIdx]));
            RootGrid.Background = new ImageBrush { ImageSource = bmp, Stretch = Stretch.Uniform };
        }
        catch (Exception ex) { Log.Warning(ex, "Background set failed"); }
    }

    private void PrevBg_Click(object sender, RoutedEventArgs e) => SetBg(_bgIdx - 1);
    private void NextBg_Click(object sender, RoutedEventArgs e) => SetBg(_bgIdx + 1);
    private void RandBg_Click(object sender, RoutedEventArgs e)
    {
        if (_backgroundImages.Count < 2) return;
        int idx; do { idx = Random.Shared.Next(_backgroundImages.Count); } while (idx == _bgIdx);
        SetBg(idx);
    }

    // ── Connect / Disconnect ──
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connected) { SetConnected(false); return; }
        await ConnectCurrentAsync(force: true);
    }

    /// <summary>Connect the selected agent: Alice LOCAL via the backend manager, fleet members via a reachability check.</summary>
    private async Task ConnectCurrentAsync(bool force = false)
    {
        StatusText.Text = "Connecting…";
        SetActivity("Connecting…");
        try
        {
            if (IsAliceLocalSelected)
            {
                if (force && _voice.CurrentKind == VoiceBackendKind.AliceLocal) await _voice.ReconnectAsync();
                else await _voice.SwitchToAsync(VoiceBackendKind.AliceLocal);
                SetConnected(true);
                StatusText.Text = "Alice LOCAL";
                SetActivity("Ready", "Alice LOCAL · local (Ollama)");
            }
            else if (SelectedMember is { } member)
            {
                var ok = await _chat.IsReachableAsync(member);
                SetConnected(ok);
                StatusText.Text = ok ? $"{member.DisplayName} reachable"
                                     : $"{member.DisplayName} not reachable ({(_chat.HasApiKey ? "not served / key rejected" : "no API_SERVER_KEY")})";
                SetActivity(ok ? "Ready" : "Offline", member.DisplayName);
            }
        }
        catch (Exception ex)
        {
            SetConnected(false);
            StatusText.Text = "Not connected";
            SetActivity("Offline", ex.Message);
            ShowError($"Connect failed: {ex.Message}");
            Log.Error(ex, "Connect failed");
        }
        UpdateStatusPanel();
    }

    private Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void SetConnected(bool connected)
    {
        _connected = connected;
        ConnDot.Fill = connected ? Res("AppGreen") : Res("AppTextDim");
        ConnText.Text = connected ? "Disconnect" : "Connect";
        PresenceText.Text = connected ? "here with you" : "offline";
        PresenceDot.Fill = connected ? Res("AppGreen") : Res("AppTextDim");
        ResetBtn.IsEnabled = connected;
        SendButton.IsEnabled = connected;
        TalkButton.IsEnabled = connected;
        PStageWs.Foreground = connected ? Res("AppGreen") : Res("AppTextDim");
        WsDisplay.Text = connected ? "api·on" : "api·off";
        UpdateStatusPanel();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        CancelTurn();
        TranscriptPanel.Children.Clear();
        _turnSeq = 0;
        // Clear the model-side history too, not just the UI.
        try
        {
            if (IsAliceLocalSelected) await _voice.SendCommandAsync("reset");
            else _chat.ResetConversation(SelectedMember);
        }
        catch (Exception ex) { Log.Warning(ex, "Reset failed"); }
        StatusText.Text = "Ready";
        AppendSystem("Conversation reset");
    }

    // ── Per-member voice assignment ──
    private void LoadMemberVoices()
    {
        try
        {
            if (!File.Exists(VoiceAssignmentsPath)) return;
            var json = File.ReadAllText(VoiceAssignmentsPath);
            var map = JsonSerializer.Deserialize<Dictionary<string, VoiceProfileDto>>(json);
            if (map is null) return;
            _memberVoices.Clear();
            foreach (var (name, dto) in map)
                _memberVoices[name] = new VoiceProfile(dto.Provider, dto.Voice);
            AddLog($"Voice assignments loaded: {_memberVoices.Count}");
            RestoreMemberVoice();
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to load voice assignments"); }
    }

    private void SaveMemberVoices()
    {
        try
        {
            var map = _memberVoices.ToDictionary(
                kv => kv.Key,
                kv => new VoiceProfileDto { Provider = kv.Value.Provider, Voice = kv.Value.Voice });
            var dir = Path.GetDirectoryName(VoiceAssignmentsPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(VoiceAssignmentsPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to save voice assignments"); }
    }

    private sealed class VoiceProfileDto
    {
        public VoiceProviderKind Provider { get; set; }
        public string Voice { get; set; } = "";
    }

    private FleetMember? SelectedMember =>
        FleetSelector.SelectedItem is ComboBoxItem item && item.Tag is string name
            ? FleetRoster.Find(name)
            : null;

    private string? SelectedMemberName =>
        FleetSelector.SelectedItem is ComboBoxItem item && item.Tag is string name ? name : null;

    private bool IsAliceLocalSelected =>
        FleetSelector.SelectedItem is ComboBoxItem item && item.Tag is string name && name == "alice_local";

    private string TargetDisplayName => IsAliceLocalSelected ? "Alice LOCAL" : SelectedMember?.DisplayName ?? "Alice";

    private VoiceProviderKind? SelectedProvider =>
        VoiceProviderSelect.SelectedItem is ComboBoxItem item && item.Tag is VoiceProviderKind kind ? kind : null;

    private VoiceProfile CurrentMemberProfile
    {
        get
        {
            var name = SelectedMemberName;
            if (name is not null && _memberVoices.TryGetValue(name, out var p)) return p;
            var voice = VoiceSelect.SelectedItem is ComboBoxItem vci && vci.Tag is string v ? v : "";
            return new VoiceProfile(SelectedProvider ?? VoiceProviderKind.Sapi, voice);
        }
    }

    // ── Voice provider + voice select ──
    private async Task LoadVoicesForSelectedProviderAsync(string? select = null)
    {
        try
        {
            var kind = SelectedProvider;
            if (kind is null) return;
            var provider = _voiceProvider.Providers.FirstOrDefault(p => p.Kind == kind);
            if (provider is null) return;
            var voices = await provider.ListVoicesAsync();
            VoiceSelect.Items.Clear();
            foreach (var v in voices.OrderBy(x => x))
                VoiceSelect.Items.Add(new ComboBoxItem { Content = v.Replace(".wav", ""), Tag = v });
            if (VoiceSelect.Items.Count > 0)
            {
                var preferred = select is not null && voices.Contains(select) ? select
                    : voices.FirstOrDefault(v => v.Contains("female_06")) ?? voices[0];
                foreach (var item in VoiceSelect.Items)
                    if (item is ComboBoxItem ci && ci.Tag?.ToString() == preferred) { VoiceSelect.SelectedItem = ci; break; }
            }
            AddLog($"Voices loaded: {voices.Count} ({provider.DisplayName})");
        }
        catch (Exception ex) { AddLog($"Voices: using defaults ({ex.Message})"); }
        UpdateStatusPanel();
    }

    private bool _restoringVoice;

    /// <summary>Put the selected member's saved provider + voice into the dropdowns.</summary>
    private async void RestoreMemberVoice()
    {
        if (SelectedMemberName is not string name || !_memberVoices.TryGetValue(name, out var saved)) return;
        _restoringVoice = true;
        try
        {
            foreach (var item in VoiceProviderSelect.Items)
                if (item is ComboBoxItem pi && pi.Tag is VoiceProviderKind pk && pk == saved.Provider)
                { VoiceProviderSelect.SelectedItem = pi; break; }
            await LoadVoicesForSelectedProviderAsync(saved.Voice);
        }
        finally { _restoringVoice = false; }
        UpdateEngineChips();
    }

    private async void VoiceProviderSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceProviderSelect.SelectedItem is not ComboBoxItem ci || ci.Tag is not VoiceProviderKind kind) return;
        UpdateEngineChips();
        if (_initializing || _restoringVoice) return;
        if (SelectedMemberName is string name)
        {
            _memberVoices[name] = new VoiceProfile(kind, "");
            SaveMemberVoices();
        }
        AddLog($"Voice provider: {kind}");
        await LoadVoicesForSelectedProviderAsync();
    }

    private void VoiceSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceSelect.SelectedItem is not ComboBoxItem ci || ci.Tag is not string voice) return;
        if (!_initializing && SelectedMemberName is string name && SelectedProvider is VoiceProviderKind kind)
        {
            _memberVoices[name] = new VoiceProfile(kind, voice);
            SaveMemberVoices();
        }
        AddLog($"Voice: {voice.Replace(".wav", "").Replace("_", " ")}");
        UpdateStatusPanel();
    }

    // ── Test TTS ──
    private async void TestTts_Click(object sender, RoutedEventArgs e)
    {
        var voice = VoiceSelect.SelectedItem is ComboBoxItem vci && vci.Tag is string v ? v : "";
        var provider = SelectedProvider ?? VoiceProviderKind.Sapi;
        var label = $"{provider} · {voice.Replace(".wav", "").Replace("_", " ")}";
        AppendSystem($"voice test — {label}");
        StatusText.Text = "Speaking…";
        SetActivity("Speaking…", label);
        PStageTts.Foreground = Res("AppGreen");
        try
        {
            await _voiceProvider.SpeakAsync(new VoiceProfile(provider, voice), "Hello Master. This is a voice quality test.");
            StatusText.Text = "Ready";
            SetActivity("Ready");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Voice test failed";
            ShowError($"Voice test failed ({provider}): {ex.Message}");
            Log.Warning(ex, "TTS test failed");
        }
        finally { PStageTts.Foreground = Res("AppTextDim"); }
    }

    // ── Modes ──
    private void ModePtt_Click(object sender, RoutedEventArgs e) => SetMode("ptt");
    private void ModeWake_Click(object sender, RoutedEventArgs e) => SetMode("ptt"); // wake word not implemented (button disabled)
    private void ModeText_Click(object sender, RoutedEventArgs e) => SetMode("text");
    private void ModeSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == "soul") { SetMode(_lastNonSoulMode); return; }
        if (!IsAliceLocalSelected)
        {
            StatusText.Text = "SOUL editor is only available on Alice LOCAL";
            AppendSystem("The SOUL editor edits Alice LOCAL's own SOUL — select Alice LOCAL as the Agent first.");
            return;
        }
        SetMode("soul");
    }

    private void SetMode(string mode)
    {
        if (mode != "soul" && _mode != "soul") _lastNonSoulMode = mode;
        _mode = mode;
        ModePttBtn.Style = (Style)Application.Current.Resources[mode == "ptt" ? "ChipButtonActive" : "ChipButton"];
        ModeWakeBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        ModeTextBtn.Style = (Style)Application.Current.Resources[mode == "text" ? "ChipButtonActive" : "ChipButton"];
        ModeSoulBtn.Style = (Style)Application.Current.Resources[mode == "soul" ? "ChipButtonActive" : "ChipButton"];

        TranscriptScroll.Visibility = mode == "soul" ? Visibility.Collapsed : Visibility.Visible;
        SoulEditor.Visibility = mode == "soul" ? Visibility.Visible : Visibility.Collapsed;
        TextInputRow.Visibility = mode == "text" ? Visibility.Visible : Visibility.Collapsed;
        WakeRow.Visibility = Visibility.Collapsed;
        PttRow.Visibility = mode == "ptt" ? Visibility.Visible : Visibility.Collapsed;

        if (mode == "soul") _ = LoadSoulAsync();
        if (mode != "ptt" && _ptt.Current != PushToTalkGesture.State.Idle)
        {
            _ptt.Reset();
            _utterance.Cancel();
            _ = StopMicAsync();
        }
        AddLog($"Mode: {mode}");
    }

    // ── SOUL editor ──
    private async Task LoadSoulAsync()
    {
        SoulStatus.Foreground = Res("AppTextDim");
        try
        {
            var doc = await _soulStore.LoadAsync();
            SoulTextarea.Text = doc.Text;
            if (doc.CreatedFromTemplate)
            {
                SoulStatus.Text = $"No Alice LOCAL SOUL existed, so a neutral starter template was created at {doc.Path}. Edit it and Save. Agent SOULs are never used.";
                SoulStatus.Foreground = Res("AppYellow");
                AddLog($"Created starter Alice LOCAL SOUL at {doc.Path}");
            }
            else SoulStatus.Text = $"Editing Alice LOCAL SOUL ({doc.Path})";
        }
        catch (Exception ex)
        {
            SoulStatus.Text = $"Load failed: {ex.Message}";
            SoulStatus.Foreground = Res("AppRed");
        }
    }

    private async void SoulSave_Click(object sender, RoutedEventArgs e)
    {
        SoulSaveBtn.IsEnabled = false;
        try
        {
            var result = await _soulStore.SaveAsync(SoulTextarea.Text);
            if (_voice.Get(VoiceBackendKind.AliceLocal) is AliceLocalBackend local) await local.ReloadSoulAsync();
            SoulStatus.Text = result.BackupPath is null
                ? $"Saved {result.Path} — applies on the next reply"
                : $"Saved (backup: {Path.GetFileName(result.BackupPath)}) — applies on the next reply";
            SoulStatus.Foreground = Res("AppGreen");
            AddLog($"SOUL saved to {result.Path}" + (result.BackupPath is null ? "" : $", backup {result.BackupPath}"));
        }
        catch (Exception ex)
        {
            SoulStatus.Text = $"Save failed: {ex.Message}";
            SoulStatus.Foreground = Res("AppRed");
        }
        finally { SoulSaveBtn.IsEnabled = true; }
    }

    private void SoulClose_Click(object sender, RoutedEventArgs e) => SetMode(_lastNonSoulMode);

    // ── Mood (applies to Alice LOCAL) ──
    private void MoodNatural_Click(object sender, RoutedEventArgs e) => SetMood("natural");
    private void MoodComfort_Click(object sender, RoutedEventArgs e) => SetMood("comfort");
    private void MoodPlayful_Click(object sender, RoutedEventArgs e) => SetMood("playful");
    private void MoodFlirty_Click(object sender, RoutedEventArgs e) => SetMood("flirty");
    private void MoodFocused_Click(object sender, RoutedEventArgs e) => SetMood("focused");

    private void SetMood(string mood)
    {
        _mood = mood;
        MoodNaturalBtn.Style = (Style)Application.Current.Resources[mood == "natural" ? "ChipButtonActive" : "ChipButton"];
        MoodComfortBtn.Style = (Style)Application.Current.Resources[mood == "comfort" ? "ChipButtonActive" : "ChipButton"];
        MoodPlayfulBtn.Style = (Style)Application.Current.Resources[mood == "playful" ? "ChipButtonActive" : "ChipButton"];
        MoodFlirtyBtn.Style = (Style)Application.Current.Resources[mood == "flirty" ? "ChipButtonActive" : "ChipButton"];
        MoodFocusedBtn.Style = (Style)Application.Current.Resources[mood == "focused" ? "ChipButtonActive" : "ChipButton"];
        AddLog($"Mood → {mood}");
    }

    /// <summary>Mood + response profile only shape Alice LOCAL; fleet agents keep their own style.</summary>
    private void UpdateStyleControls()
    {
        var local = IsAliceLocalSelected;
        var tip = local ? null : "Applies to Alice LOCAL only — fleet agents use their own Hermes profile settings.";
        foreach (var b in new[] { MoodNaturalBtn, MoodComfortBtn, MoodPlayfulBtn, MoodFlirtyBtn, MoodFocusedBtn, ProfQuickBtn, ProfNaturalBtn, ProfFullBtn })
        {
            b.IsEnabled = local;
            ToolTipService.SetToolTip(b, tip);
        }
    }

    // ── Wake sensitivity (wake word not implemented; row hidden) ──
    private void WakeSensitivity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SensitivityValue is not null)
            SensitivityValue.Text = e.NewValue.ToString("0.00");
    }

    // ── Tuning ──
    private void ProfQuick_Click(object sender, RoutedEventArgs e) => SetProfile("quick");
    private void ProfNatural_Click(object sender, RoutedEventArgs e) => SetProfile("natural");
    private void ProfFull_Click(object sender, RoutedEventArgs e) => SetProfile("full");

    private void SetProfile(string profile)
    {
        _profile = profile;
        ProfQuickBtn.Style = (Style)Application.Current.Resources[profile == "quick" ? "ChipButtonActive" : "ChipButton"];
        ProfNaturalBtn.Style = (Style)Application.Current.Resources[profile == "natural" ? "ChipButtonActive" : "ChipButton"];
        ProfFullBtn.Style = (Style)Application.Current.Resources[profile == "full" ? "ChipButtonActive" : "ChipButton"];
        ProfHint.Text = profile switch
        {
            "quick" => $"1–2 short sentences · tightest loop · max {ReplyStyle.MaxTokens("quick")} tokens",
            "full" => $"longer, richer replies · slower loop · max {ReplyStyle.MaxTokens("full")} tokens",
            _ => $"2–3 sentences · conversational default · max {ReplyStyle.MaxTokens("natural")} tokens"
        };
        AddLog($"Profile → {profile}");
    }

    // Engine chips select the voice provider for the current agent.
    private void TtsAlltalk_Click(object sender, RoutedEventArgs e) => SelectProvider(VoiceProviderKind.AllTalk);
    private void TtsPiper_Click(object sender, RoutedEventArgs e) => SelectProvider(VoiceProviderKind.Piper);

    private void SelectProvider(VoiceProviderKind kind)
    {
        foreach (var item in VoiceProviderSelect.Items)
            if (item is ComboBoxItem pi && pi.Tag is VoiceProviderKind pk && pk == kind)
            { VoiceProviderSelect.SelectedItem = pi; break; }
    }

    private void UpdateEngineChips()
    {
        var kind = SelectedProvider;
        TtsAlltalkBtn.Style = (Style)Application.Current.Resources[kind == VoiceProviderKind.AllTalk ? "ChipButtonActive" : "ChipButton"];
        TtsPiperBtn.Style = (Style)Application.Current.Resources[kind == VoiceProviderKind.Piper ? "ChipButtonActive" : "ChipButton"];
        UpdateStatusPanel();
    }

    // ── Context config sliders (disabled: not implemented) ──
    private void CtxPoints_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (SCtxPoints is not null) SCtxPoints.Text = ((int)e.NewValue).ToString(); }
    private void CtxHoncho_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (SCtxHoncho is not null) SCtxHoncho.Text = ((int)e.NewValue).ToString(); }
    private void CtxChars_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (SCtxChars is not null) SCtxChars.Text = ((int)e.NewValue).ToString(); }

    // ── Collapsible panels ──
    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        _logOpen = !_logOpen;
        LogArea.Visibility = _logOpen ? Visibility.Visible : Visibility.Collapsed;
        LogArea.MaxHeight = _logOpen ? 120 : 0;
        LogToggle.Content = (_logOpen ? "▾" : "▸") + "  Event Log";
    }
    private void HelpToggle_Click(object sender, RoutedEventArgs e)
    {
        _helpOpen = !_helpOpen;
        HelpArea.Visibility = _helpOpen ? Visibility.Visible : Visibility.Collapsed;
        HelpToggle.Content = (_helpOpen ? "▾" : "▸") + "  Keywords & Tools";
    }
    private void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = !_settingsOpen;
        SettingsArea.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggle.Content = (_settingsOpen ? "▾" : "▸") + "  Settings";
        if (_settingsOpen) UpdateStatusPanel();
    }
    private void MetricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _metricsOpen = !_metricsOpen;
        MetricsArea.Visibility = _metricsOpen ? Visibility.Visible : Visibility.Collapsed;
        MetricsToggle.Content = (_metricsOpen ? "▾" : "▸") + "  Latency Metrics";
    }
    private void TuningToggle_Click(object sender, RoutedEventArgs e)
    {
        _tuningOpen = !_tuningOpen;
        TuningArea.Visibility = _tuningOpen ? Visibility.Visible : Visibility.Collapsed;
        TuningToggle.Content = (_tuningOpen ? "▾" : "▸") + "  Tuning";
    }

    // ── Backend events (streaming text, status) ──
    private void OnVoiceEvent(BackendEvent evt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (evt.Type)
            {
                case "reply_partial":
                    // Only the turn that is currently running may update its bubble.
                    if (_streamTurnId != 0 && _streamTurnId == _turnId && evt.Text is { Length: > 0 } partial)
                    {
                        if (_streamText is null) (_streamText, _streamRemember) = AppendAlice(partial, streaming: true);
                        else _streamText.Text = partial;
                        PStageLlm.Foreground = Res("AppGreen");
                        ScrollToEnd();
                    }
                    break;
                case "tool_call":
                    AddLog($"Tool: {evt.Text}");
                    SetActivity("Thinking…", $"using {evt.Text}");
                    break;
                case "backend_ready":
                case "backend_switched":
                case "reset_ack":
                case "model_info":
                    if (evt.Text is not null) AddLog(evt.Text);
                    break;
                case "error":
                    PStageErr.Text = evt.Text ?? "";
                    ShowError(evt.Text ?? "Error");
                    break;
            }
        });
    }

    // ── Microphone ──
    private void OnMicFrame(float[] pcm16k)
    {
        var rms = _utterance.Append(pcm16k);
        _vuLevel = Math.Max(_vuLevel * 0.6f, rms);
    }

    private async Task StartMicAsync()
    {
        _utterance.Start();
        if (!_micRunning)
        {
            await _mic.StartAsync();
            _micRunning = true;
        }
        PStageMic.Foreground = Res("AppGreen");
        MicTag.Text = "🎤 On";
        MicTag.Foreground = Res("AppGreen");
        _vuTimer ??= CreateVuTimer();
        _vuTimer.Start();
    }

    private async Task StopMicAsync()
    {
        _vuTimer?.Stop();
        _vuLevel = 0;
        VuFill.Width = 0;
        if (_micRunning)
        {
            _micRunning = false;
            try { await _mic.StopAsync(); } catch (Exception ex) { Log.Warning(ex, "Mic stop failed"); }
        }
        PStageMic.Foreground = Res("AppTextDim");
        MicTag.Text = "🎤 Off";
        MicTag.Foreground = Res("AppRed");
    }

    private DispatcherQueueTimer CreateVuTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(50);
        t.Tick += (_, _) =>
        {
            // ~-40 dBFS..0 dBFS mapped onto the 100px meter.
            var db = 20 * Math.Log10(Math.Max(_vuLevel, 1e-5));
            VuFill.Width = Math.Clamp((db + 40) / 40 * 100, 0, 100);
            _vuLevel *= 0.85f;
            if (_utterance.IsFull)
            {
                AddLog("Recording cap (120 s) reached — sending");
                _ptt.Reset();
                _ = FinishUtteranceAsync();
            }
        };
        return t;
    }

    // ── Push-to-talk ──
    private async void TalkButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerDrivenClick = true;
        await ApplyPttAsync(_ptt.Press(DateTime.UtcNow));
    }

    private async void TalkButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Button raises Click before routed handlers run; this pointer-driven Click was already ignored.
        _pointerDrivenClick = false;
        await ApplyPttAsync(_ptt.Release(DateTime.UtcNow));
    }

    private async void TalkButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pointerDrivenClick = false;
        await ApplyPttAsync(_ptt.Cancel());
    }

    /// <summary>Keyboard activation (Space/Enter) toggles; pointer clicks are handled by press/release.</summary>
    private async void TalkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pointerDrivenClick) return;
        await ApplyPttAsync(_ptt.Toggle());
    }

    private async Task ApplyPttAsync(PttAction action)
    {
        try
        {
            switch (action)
            {
                case PttAction.StartRecording:
                    // Talking over Alice interrupts her and cancels the turn in flight.
                    CancelTurn();
                    await StartMicAsync();
                    TalkButton.Content = _ptt.Current == PushToTalkGesture.State.Latched ? "■ Send" : "🎤 Release to send";
                    StatusText.Text = "Listening…";
                    SetActivity("Listening…");
                    break;
                case PttAction.StopAndTranscribe:
                    await FinishUtteranceAsync();
                    break;
                default:
                    if (_ptt.Current == PushToTalkGesture.State.Latched) TalkButton.Content = "■ Send";
                    break;
            }
        }
        catch (Exception ex)
        {
            _ptt.Reset();
            _utterance.Cancel();
            await StopMicAsync();
            TalkButton.Content = "🎤 Talk";
            ShowError($"Microphone error: {ex.Message}");
            Log.Error(ex, "PTT failed");
        }
    }

    private async Task FinishUtteranceAsync()
    {
        var pcm = _utterance.Stop();
        await StopMicAsync();
        TalkButton.Content = "🎤 Talk";
        if (pcm is null)
        {
            StatusText.Text = "Too short — hold Talk while you speak";
            SetActivity("Ready");
            return;
        }

        var turn = BeginTurn();
        var ct = turn.Token;
        StatusText.Text = "Transcribing…";
        SetActivity("Transcribing…", $"{pcm.Length / 16000.0:F1}s of audio");
        PStageStt.Foreground = Res("AppYellow");
        SttResult stt;
        var sw = Stopwatch.StartNew();
        try
        {
            stt = await _voice.TranscribeAsync(pcm, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            PStageStt.Foreground = Res("AppRed");
            StatusText.Text = "Transcription failed";
            SetActivity("Ready");
            ShowError($"Speech-to-text failed: {ex.Message}");
            Log.Error(ex, "STT failed");
            return;
        }
        PStageStt.Foreground = Res("AppGreen");
        if (string.IsNullOrWhiteSpace(stt.Text))
        {
            StatusText.Text = "Didn't catch that";
            SetActivity("Ready");
            return;
        }
        await RunTurnAsync(stt.Text.Trim(), sw.ElapsedMilliseconds, turn);
    }

    // ── Turns (typed and spoken share one path) ──
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
        cts.Dispose();
        try { _tts.Stop(); } catch { }
        _streamTurnId = 0;
        _streamText = null;
        _streamRemember = null;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendChatAsync();
        }
    }

    private async Task SendChatAsync()
    {
        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || !_connected) return;
        if (SelectedMember is null && !IsAliceLocalSelected) return;
        ChatInput.Text = "";
        await RunTurnAsync(text, sttMs: 0, BeginTurn());
    }

    /// <summary>One conversational turn: LLM (streamed for Alice LOCAL) then speech.</summary>
    private async Task RunTurnAsync(string text, long sttMs, CancellationTokenSource turn)
    {
        var ct = turn.Token;
        var myTurn = _turnId;
        var target = TargetDisplayName;
        var total = Stopwatch.StartNew();
        AppendUser($"{text} → {target}");
        StatusText.Text = $"Talking to {target}…";
        SetActivity("Thinking…", text);
        PStageLlm.Foreground = Res("AppYellow");

        string reply;
        long ttft = -1;
        var llm = Stopwatch.StartNew();
        try
        {
            if (IsAliceLocalSelected)
            {
                _streamTurnId = myTurn;
                _streamText = null;
                void OnFirst(BackendEvent ev) { if (ev.Type == "reply_partial" && ttft < 0) ttft = llm.ElapsedMilliseconds; }
                _voice.EventReceived += OnFirst;
                try { reply = await _voice.AskAsync(text, ReplyStyle.Build(_mood, _profile), ct); }
                finally { _voice.EventReceived -= OnFirst; }
            }
            else
            {
                var member = SelectedMember!;
                reply = await _chat.SendAsync(member, text, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AddLog("Turn cancelled");
            return;
        }
        catch (Exception ex)
        {
            if (myTurn == _turnId) { _streamTurnId = 0; _streamText = null; }
            PStageLlm.Foreground = Res("AppRed");
            AppendSystem($"{target}: (no reply — {ex.Message})");
            StatusText.Text = "Chat failed";
            SetActivity("Ready");
            ShowError($"{target}: {ex.Message}");
            Log.Error(ex, "Turn to {Target} failed", target);
            return;
        }
        llm.Stop();
        if (myTurn != _turnId) return; // superseded by a newer turn

        // Finalise the (possibly streamed) bubble with the final reply.
        if (_streamText is not null)
        {
            _streamText.Text = reply;
            if (_streamRemember is not null) _streamRemember.Visibility = Visibility.Visible;
        }
        else AppendAlice(reply);
        _streamTurnId = 0;
        _streamText = null;
        _streamRemember = null;
        PStageLlm.Foreground = Res("AppGreen");

        long ttsMs = 0;
        if (!string.IsNullOrWhiteSpace(reply))
        {
            StatusText.Text = "Speaking…";
            SetActivity("Speaking…", target);
            PStageTts.Foreground = Res("AppGreen");
            var tts = Stopwatch.StartNew();
            try { await _voiceProvider.SpeakAsync(CurrentMemberProfile, reply, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowError($"Speech output failed: {ex.Message}");
                Log.Warning(ex, "TTS failed");
            }
            ttsMs = tts.ElapsedMilliseconds;
            PStageTts.Foreground = Res("AppTextDim");
        }
        if (myTurn != _turnId) return;

        RecordTiming(new TurnTiming(target, sttMs, llm.ElapsedMilliseconds, ttft < 0 ? llm.ElapsedMilliseconds : ttft, ttsMs, sttMs + total.ElapsedMilliseconds));
        StatusText.Text = $"Replied from {target}";
        SetActivity("Ready", target);
    }

    private void RecordTiming(TurnTiming t)
    {
        _timings.Add(t);
        if (_timings.Count > 20) _timings.RemoveAt(0);
        MTotal.Text = $"{t.TotalMs} ms";
        MStt.Text = t.SttMs > 0 ? $"{t.SttMs} ms" : "—";
        MLlm.Text = $"{t.LlmMs} ms";
        MGap.Text = $"{t.TtftMs} ms";
        MTts.Text = $"{t.TtsMs} ms";
        var avg = _timings.Average(x => (double)x.TotalMs);
        MetricsSummary.Text = $"{_timings.Count} turns · avg E2E {avg:F0} ms · avg LLM {_timings.Average(x => (double)x.LlmMs):F0} ms";
        MetricsTable.Text = string.Join("\n", _timings.TakeLast(5).Reverse()
            .Select(x => $"{x.Target}: E2E {x.TotalMs} · STT {x.SttMs} · LLM {x.LlmMs} · TTFT {x.TtftMs} · TTS {x.TtsMs} ms"));
        TuningLastTurn.Text = $"Last turn: E2E {t.TotalMs} ms · think {t.LlmMs} ms · speak {t.TtsMs} ms";
    }

    private async void FleetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        CancelTurn();
        UpdateStyleControls();
        RestoreMemberVoice();
        if (_mode == "soul" && !IsAliceLocalSelected) SetMode(_lastNonSoulMode);
        // Fleet members are reached through fleet chat; Alice LOCAL through the backend manager.
        await ConnectCurrentAsync();
    }

    // ── Transcript ──
    private void ScrollToEnd()
    {
        // Defer until layout has measured the new content, otherwise ScrollableHeight is stale.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, disableAnimation: true));
    }

    private void AppendUser(string text)
    {
        _turnSeq += 1;
        var n = _turnSeq;
        var d = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0x3B, 0x3B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 12, 4),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 600,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = $"You  #{n} · {DateTime.Now:HH:mm:ss}", FontSize = 10, Opacity = 0.5, Foreground = Res("AppText") });
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Res("AppText") });
        d.Child = stack;
        TranscriptPanel.Children.Add(d);
        ScrollToEnd();
    }

    private (TextBlock text, Button remember) AppendAlice(string text, bool streaming = false)
    {
        var n = _turnSeq;
        var d = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x00, 0x00, 0x00)),
            BorderBrush = Res("AppBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 600,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = $"{TargetDisplayName}  #{n} · {DateTime.Now:HH:mm:ss}", FontSize = 10, Opacity = 0.5, Foreground = Res("AppText") });
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Res("AppText") };
        stack.Children.Add(body);
        var remember = new Button
        {
            Content = "♥ remember",
            Style = (Style)Application.Current.Resources["ChipButton"],
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = streaming ? Visibility.Collapsed : Visibility.Visible
        };
        var forLocal = IsAliceLocalSelected;
        if (!forLocal)
        {
            remember.IsEnabled = false;
            ToolTipService.SetToolTip(remember, "Remember saves to Alice LOCAL's memory — available when talking to Alice LOCAL.");
        }
        remember.Click += (_, _) => Remember(body.Text, remember);
        stack.Children.Add(remember);
        d.Child = stack;
        TranscriptPanel.Children.Add(d);
        ScrollToEnd();
        return (body, remember);
    }

    private void AppendSystem(string text)
    {
        var pill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
            BorderBrush = Res("AppBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 700,
            Margin = new Thickness(0, 2, 0, 8)
        };
        pill.Child = new TextBlock
        {
            Text = text,
            Foreground = Res("AppTextDim"),
            FontSize = 11,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        TranscriptPanel.Children.Add(pill);
        ScrollToEnd();
    }

    /// <summary>"♥ remember": save the reply to Alice LOCAL's own Honcho memory (honcho_save).</summary>
    private async void Remember(string text, Button btn)
    {
        if (_voice.Get(VoiceBackendKind.AliceLocal) is not AliceLocalBackend local) return;
        btn.IsEnabled = false;
        btn.Content = "…";
        try
        {
            var content = text.Length > 1000 ? text[..1000] : text;
            var ok = await local.RememberAsync(content);
            btn.Content = ok ? "♥ saved" : "♥ failed";
            btn.Opacity = ok ? 0.5 : 1;
            if (!ok) btn.IsEnabled = true;
            AddLog(ok ? "Memory: saved to Alice LOCAL" : "Memory: save failed (see log)");
        }
        catch (Exception ex)
        {
            btn.Content = "♥ failed";
            btn.IsEnabled = true;
            Log.Warning(ex, "Remember failed");
        }
    }

    // ── Log / Error ──
    private void AddLog(string text)
    {
        if (LogPanel is null) return;
        LogPanel.Children.Add(new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {text}",
            Foreground = Res("AppTextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        while (LogPanel.Children.Count > 500) LogPanel.Children.RemoveAt(0);
    }

    private void ShowError(string text)
    {
        ErrorBanner.Visibility = Visibility.Visible;
        ErrorBannerText.Text = text;
        AddLog($"Error: {text}");
        _errorTimer ??= CreateErrorTimer();
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private DispatcherQueueTimer CreateErrorTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(12);
        t.IsRepeating = false;
        t.Tick += (_, _) => ErrorBanner.Visibility = Visibility.Collapsed;
        return t;
    }

    private void ErrorDismiss_Click(object sender, RoutedEventArgs e)
    {
        _errorTimer?.Stop();
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _updater.CheckForUpdateAsync();
            if (release is null) return;
            StatusText.Text = $"Update available: {release.Version}";
            Log.Information("Update {Version} available", release.Version);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background update check failed");
        }
    }

    // ── Pinned status panel + live activity line ──
    private void SetActivity(string state, string? detail = null)
    {
        if (ActivityLine is null) return;
        ActivityLine.Text = state;
        ActivityDetail.Text = detail ?? "";
        ActivityDot.Fill = state switch
        {
            "Listening…" or "Transcribing…" or "Thinking…" or "Speaking…" or "Connecting…" => Res("AppYellow"),
            "Ready" => Res("AppGreen"),
            _ => Res("AppTextDim")
        };
    }

    private void UpdateStatusPanel()
    {
        if (StatusAgent is null) return;
        var local = IsAliceLocalSelected;
        StatusAgent.Text = TargetDisplayName;
        StatusBackend.Text = local ? "local (Ollama)" : "fleet (Hermes API)";
        StatusProvider.Text = SelectedProvider?.ToString() ?? "—";
        var voice = VoiceSelect.SelectedItem is ComboBoxItem vci && vci.Tag is string v ? v.Replace(".wav", "").Replace("_", " ") : "—";
        StatusVoice.Text = voice;
        StatusConn.Text = _connected ? "connected" : "offline";
        StatusConn.Foreground = _connected ? Res("AppGreen") : Res("AppTextDim");

        var model = local ? _voice.Get(VoiceBackendKind.AliceLocal).Settings.Model : SelectedMember?.Profile ?? "—";
        LlmModeDisplay.Text = local ? "alice_local" : SelectedMember?.Name ?? "—";
        SettingsModel.Text = $"Brain: {(local ? "Ollama" : "Hermes")} · {model} · STT: {_voice.Stt.Status} · TTS: {SelectedProvider?.ToString() ?? "—"} · Voice: {voice} · Version: {_updater.CurrentVersion}";
        FooterAuth.Text = local ? "Local session (no API key needed)"
            : _chat.HasApiKey ? "API key loaded" : "No API_SERVER_KEY found";
    }

    private void OnWindowClosed()
    {
        try
        {
            CancelTurn();
            _vuTimer?.Stop();
            _errorTimer?.Stop();
            _mic.AudioFrameCaptured -= OnMicFrame;
            _voice.EventReceived -= OnVoiceEvent;
        }
        catch (Exception ex) { Log.Warning(ex, "Window close cleanup failed"); }
    }
}
