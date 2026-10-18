using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Audio;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using AliceVoice.Inference;
using System.Diagnostics;
using System.Text.Json;

namespace AliceVoice.App;

public sealed partial class MainWindow : Window
{
    private readonly IGpuSelector _gpu;
    private readonly IWhisperEngine _whisper;
    private readonly IAppUpdater _updater;
    private readonly IVoiceBackendManager _voice;
    private readonly IMicrophoneCapture _mic;
    private readonly ITtsPlayback _tts;
    private readonly IFleetChat _chat;
    private readonly IVoiceProviderManager _voiceProvider;

    private bool _talking;
    private string _mood = "natural";
    private string _mode = "ptt";
    private string _lastNonSoulMode = "ptt";
    private string _profile = "natural";
    private string _ttsEngine = "alltalk";
    private int _turnSeq;
    private bool _connected;
    private bool _logOpen, _helpOpen, _settingsOpen, _metricsOpen, _tuningOpen, _clipsOpen;
    private readonly List<string> _backgroundImages = new();
    private int _bgIdx = -1;

    // Per-fleet-member voice assignment: member name -> { provider, voice }.
    private readonly Dictionary<string, VoiceProfile> _memberVoices = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string VoiceAssignmentsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliceVoice", "voice-assignments.json");

    public MainWindow()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    for (var e = ex; e != null; e = e.InnerException)
                        sb.AppendLine(e.GetType().FullName + ": " + e.Message);
                    System.IO.File.WriteAllText(@"C:\Users\AHC.DAEMONLINKS\AppData\Local\AliceVoice\xaml_crash.txt", ex.ToString());
                }
                catch { }
                throw;
            }

        _gpu = App.Services.GetRequiredService<IGpuSelector>();
        _whisper = App.Services.GetRequiredService<IWhisperEngine>();
        _updater = App.Services.GetRequiredService<IAppUpdater>();
        _voice = App.Services.GetRequiredService<IVoiceBackendManager>();
        _mic = App.Services.GetRequiredService<IMicrophoneCapture>();
        _tts = App.Services.GetRequiredService<ITtsPlayback>();
        _chat = App.Services.GetRequiredService<IFleetChat>();
        _voiceProvider = App.Services.GetRequiredService<IVoiceProviderManager>();

        _voice.EventReceived += OnVoiceEvent;
        _voice.AudioReceived += OnVoiceAudio;
        _mic.AudioFrameCaptured += OnMicFrame;

        // Populate the fleet selector.
                foreach (var m in FleetRoster.Members)
                                    FleetSelector.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.Name });
                                FleetSelector.SelectedIndex = 0;

        // Populate the voice provider selector from the registered providers.
                foreach (var p in _voiceProvider.Providers)
                                    VoiceProviderSelect.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p.Kind });
                            }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                try { AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800)); } catch { }
                LoadBackgrounds();
                LoadMemberVoices();
                await _whisper.WarmUpAsync();
            await _voice.SwitchToAsync(VoiceBackendKind.AliceVoiceAgent);
            SetConnected(true);
            Log.Information("Alice Voice started");
            _ = CheckForUpdatesAsync();
            _ = LoadVoicesForSelectedProviderAsync();
            AppendSystem("Talk to me, Master.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error";
            Log.Error(ex, "Failed to initialise Alice Voice");
        }
    }

    // ── Background images ──
    private void LoadBackgrounds()
    {
        try
        {
            var dir = @"C:\Users\AHC.DAEMONLINKS\ahc-the-beast\voice\Mirrored";
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
                RootGrid.Background = new Microsoft.UI.Xaml.Media.ImageBrush
                {
                    ImageSource = bmp,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
                };
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
        try
        {
            if (_connected) { SetConnected(false); return; }
            StatusText.Text = "Connecting…";
            await _voice.SwitchToAsync(VoiceBackendKind.AliceVoiceAgent);
            SetConnected(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connect failed: {ex.Message}";
            Log.Error(ex, "Connect failed");
        }
    }

    private void SetConnected(bool connected)
    {
        _connected = connected;
        ConnDot.Fill = connected
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
        ConnText.Text = connected ? "Disconnect" : "Connect";
        PresenceText.Text = connected ? "here with you" : "offline";
        PresenceDot.Fill = connected
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
        ResetBtn.IsEnabled = connected;
        SendButton.IsEnabled = connected;
        PStageWs.Foreground = connected
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
        WsDisplay.Text = connected ? "ws·on" : "ws·off";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        TranscriptPanel.Children.Clear();
        _turnSeq = 0;
        StatusText.Text = "Ready";
        AppendSystem("Conversation reset");
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        SetConnected(false);
        StatusText.Text = "Logged out";
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

    private VoiceProviderKind? SelectedProvider =>
        VoiceProviderSelect.SelectedItem is ComboBoxItem item && item.Tag is VoiceProviderKind kind ? kind : null;

    private VoiceProfile CurrentMemberProfile
    {
        get
        {
            var name = SelectedMemberName;
            if (name is not null && _memberVoices.TryGetValue(name, out var p)) return p;
            return new VoiceProfile(SelectedProvider ?? VoiceProviderKind.AllTalk, "");
        }
    }

    // ── Voice provider + voice select ──
    private async Task LoadVoicesForSelectedProviderAsync()
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
                var preferred = voices.FirstOrDefault(v => v.Contains("female_06")) ?? voices[0];
                foreach (var item in VoiceSelect.Items)
                    if (item is ComboBoxItem ci && ci.Tag?.ToString() == preferred) { VoiceSelect.SelectedItem = ci; break; }
            }
            AddLog($"Voices loaded: {voices.Count} ({provider.DisplayName})");
        }
        catch (Exception ex) { AddLog($"Voices: using defaults ({ex.Message})"); }
    }

    private async void VoiceProviderSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceProviderSelect.SelectedItem is not ComboBoxItem ci || ci.Tag is not VoiceProviderKind kind) return;
        _ttsEngine = kind == VoiceProviderKind.AllTalk ? "alltalk" : "sapi";
        var name = SelectedMemberName;
        if (name is not null)
        {
            var profile = CurrentMemberProfile;
            _memberVoices[name] = new VoiceProfile(kind, profile.Voice);
            SaveMemberVoices();
        }
        AddLog($"Voice provider: {kind}");
        await LoadVoicesForSelectedProviderAsync();
    }

    private void VoiceSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceSelect.SelectedItem is not ComboBoxItem ci || ci.Tag is not string voice) return;
        var name = SelectedMemberName;
        if (name is not null)
        {
            var profile = CurrentMemberProfile;
            _memberVoices[name] = new VoiceProfile(profile.Provider, voice);
            SaveMemberVoices();
        }
        AddLog($"Voice: {voice.Replace(".wav", "").Replace("_", " ")}");
    }

    // ── Test TTS ──
    private async void TestTts_Click(object sender, RoutedEventArgs e)
    {
        var voice = VoiceSelect.SelectedItem is ComboBoxItem vci && vci.Tag is string v ? v : "";
        AppendSystem($"voice test — {voice.Replace(".wav", "").Replace("_", " ")}");
        StatusText.Text = "Speaking…";
        PStageTts.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
        await _voiceProvider.SpeakAsync(CurrentMemberProfile, "Hello Master. This is a voice quality test.");
        PStageTts.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
        StatusText.Text = "Ready";
    }

    // ── Modes ──
    private void ModePtt_Click(object sender, RoutedEventArgs e) => SetMode("ptt");
    private void ModeWake_Click(object sender, RoutedEventArgs e) => SetMode("wake");
    private void ModeText_Click(object sender, RoutedEventArgs e) => SetMode("text");
    private void ModeSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == "soul") { SetMode(_lastNonSoulMode); return; }
        SetMode("soul");
    }

    private void SetMode(string mode)
    {
        if (mode != "soul" && _mode != "soul") _lastNonSoulMode = mode;
        _mode = mode;
        ModePttBtn.Style = (Style)Application.Current.Resources[mode == "ptt" ? "ChipButtonActive" : "ChipButton"];
        ModeWakeBtn.Style = (Style)Application.Current.Resources[mode == "wake" ? "ChipButtonActive" : "ChipButton"];
        ModeTextBtn.Style = (Style)Application.Current.Resources[mode == "text" ? "ChipButtonActive" : "ChipButton"];
        ModeSoulBtn.Style = (Style)Application.Current.Resources[mode == "soul" ? "ChipButtonActive" : "ChipButton"];

        TranscriptScroll.Visibility = mode == "soul" ? Visibility.Collapsed : Visibility.Visible;
        SoulEditor.Visibility = mode == "soul" ? Visibility.Visible : Visibility.Collapsed;
        TextInputRow.Visibility = mode == "text" ? Visibility.Visible : Visibility.Collapsed;
        WakeRow.Visibility = mode == "wake" ? Visibility.Visible : Visibility.Collapsed;
        PttRow.Visibility = mode == "ptt" ? Visibility.Visible : Visibility.Collapsed;

        if (mode == "soul") _ = LoadSoulAsync();
        if (mode != "ptt" && _talking) { _ = _mic.StopAsync(); _talking = false; TalkButton.Content = "🎤 Talk"; }
        AddLog($"Mode: {mode}");
    }

    // ── SOUL editor ──
    private async Task LoadSoulAsync()
    {
        try
        {
            var path = @"C:\Users\AHC.DAEMONLINKS\AppData\Local\hermes\profiles\alice_voice\SOUL.md";
            if (File.Exists(path)) SoulTextarea.Text = await File.ReadAllTextAsync(path);
            SoulStatus.Text = "Loaded";
        }
        catch (Exception ex) { SoulStatus.Text = $"Load failed: {ex.Message}"; }
    }

    private async void SoulSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = @"C:\Users\AHC.DAEMONLINKS\AppData\Local\hermes\profiles\alice_voice\SOUL.md";
            await File.WriteAllTextAsync(path, SoulTextarea.Text);
            SoulStatus.Text = "SOUL saved to alice_voice profile — applies on next reply";
            SoulStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
        }
        catch (Exception ex) { SoulStatus.Text = $"Save failed: {ex.Message}"; }
    }

    private void SoulClose_Click(object sender, RoutedEventArgs e) => SetMode(_lastNonSoulMode);

    // ── Mood ──
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

    // ── Wake sensitivity ──
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
            "quick" => "1–2 short sentences · tightest loop · max 160 tokens",
            "full" => "longer, richer replies · slower loop · max 1024 tokens",
            _ => "2–3 sentences · conversational default · max 320 tokens"
        };
        AddLog($"Profile → {profile}");
    }

    private void TtsAlltalk_Click(object sender, RoutedEventArgs e) => SetEngine("alltalk");
    private void TtsPiper_Click(object sender, RoutedEventArgs e) => SetEngine("piper");

    private void SetEngine(string engine)
    {
        _ttsEngine = engine;
        TtsAlltalkBtn.Style = (Style)Application.Current.Resources[engine == "alltalk" ? "ChipButtonActive" : "ChipButton"];
        TtsPiperBtn.Style = (Style)Application.Current.Resources[engine == "piper" ? "ChipButtonActive" : "ChipButton"];
        AddLog($"TTS engine → {engine}");
    }

    // ── Context config sliders ──
    private void CtxPoints_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => SCtxPoints.Text = ((int)e.NewValue).ToString();
    private void CtxHoncho_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => SCtxHoncho.Text = ((int)e.NewValue).ToString();
    private void CtxChars_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => SCtxChars.Text = ((int)e.NewValue).ToString();

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
    private void ClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        _clipsOpen = !_clipsOpen;
        ClipsArea.Visibility = _clipsOpen ? Visibility.Visible : Visibility.Collapsed;
        ClipsToggle.Content = (_clipsOpen ? "▾" : "▸") + "  Clips";
        if (_clipsOpen) RenderClips();
    }

    private void RenderClips()
    {
        ClipsList.Children.Clear();
        var clips = new[]
        {
            ("2026-09-07_alice_i_want_you_master.mp4", "I want you, Master"),
            ("2026-09-07_alice_i_need_you_master.mp4", "I need you, Master"),
            ("2026-09-07_alice_wan_speaks.mp4", "a soft word for you"),
            ("2026-09-06_alice_short_good_girl.mp4", "good girl"),
            ("2026-09-06_alice_first_speaking_slowstrip.mp4", "first speaking — slow strip"),
            ("2026-09-06_alice_whimper_loop.mp4", "a little whimper"),
            ("2026-09-06_alice_beg_deep.mp4", "begging, deep")
        };
        foreach (var (file, label) in clips)
        {
            var btn = new Button
            {
                Content = $"▶  {label}",
                Style = (Style)Application.Current.Resources["ChipButton"],
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = file
            };
            btn.Click += (_, _) => PlayClip(file, label);
            ClipsList.Children.Add(btn);
        }
    }

    private void PlayClip(string file, string label)
    {
        var path = Path.Combine(@"C:\Users\AHC.DAEMONLINKS\ahc-the-beast\voice\clips", file);
        if (File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { AddLog($"Clip failed: {ex.Message}"); }
        }
        else AddLog($"Clip not found: {file}");
        AddLog($"Clip → {label}");
    }

    // ── Voice events ──
    private void OnVoiceEvent(BackendEvent evt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (evt.Type)
            {
                case "transcript":
                    AppendUser(evt.Text);
                    PStageStt.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
                    break;
                case "reply_text":
                    AppendAlice(evt.Text);
                    PStageLlm.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
                    break;
                case "tts_start":
                    PStageTts.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
                    StatusText.Text = "Speaking…";
                    break;
                case "tts_end":
                    StatusText.Text = "Ready";
                    break;
                case "backend_switched":
                    StatusText.Text = evt.Text;
                    break;
                case "error":
                    PStageErr.Text = evt.Text;
                    ShowError(evt.Text);
                    break;
            }
        });
    }

    private void OnVoiceAudio(byte[] pcm)
    {
        _tts.EnqueuePcm(pcm, 22050);
    }

    private void OnMicFrame(float[] pcm16k)
    {
        var shorts = new short[pcm16k.Length];
        for (int i = 0; i < pcm16k.Length; i++)
        {
            var v = pcm16k[i] * 32768f;
            shorts[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
        _ = _voice.SendAudioAsync(shorts);
    }

    // ── Transcript ──
    private void AppendUser(string text)
    {
        _turnSeq += 1;
        var n = _turnSeq;
        var d = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x0F, 0xFF, 0x3B, 0x3B)),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0x3B, 0x3B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 600,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = $"You  #{n} · {DateTime.Now:HH:mm:ss}", FontSize = 10, Opacity = 0.5, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppText"] });
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppText"] });
        d.Child = stack;
        TranscriptPanel.Children.Add(d);
        TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
    }

    private void AppendAlice(string text)
    {
        var n = _turnSeq;
        var d = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorder"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 12, 4),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 600,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var stack = new StackPanel();
        var who = SelectedMember?.DisplayName ?? "Alice";
        stack.Children.Add(new TextBlock { Text = $"{who}  #{n} · {DateTime.Now:HH:mm:ss}", FontSize = 10, Opacity = 0.5, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppText"] });
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppText"] });
        var remember = new Button
        {
            Content = "♥ remember",
            Style = (Style)Application.Current.Resources["ChipButton"],
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 6, 0, 0),
            Tag = text
        };
        remember.Click += (_, _) => Remember(text, remember);
        stack.Children.Add(remember);
        d.Child = stack;
        TranscriptPanel.Children.Add(d);
        TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
    }

    private void AppendSystem(string text)
    {
        TranscriptPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"],
            FontSize = 11,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2, 0, 8)
        });
        TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
    }

    private void Remember(string text, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "…";
        AddLog($"Memory: remember requested — {text[..Math.Min(40, text.Length)]}…");
        btn.Content = "♥ saved";
        btn.Opacity = 0.5;
    }

    // ── Fleet chat ──
    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        var member = SelectedMember;
        var text = ChatInput.Text?.Trim();
        if (member is null || string.IsNullOrEmpty(text)) return;

        ChatInput.Text = "";
        AppendUser($"{text} → {member.DisplayName}");
        StatusText.Text = $"Talking to {member.DisplayName}…";

        try
        {
            var reply = await _chat.SendAsync(member, text);
            AppendAlice(reply);
            StatusText.Text = $"Replied from {member.DisplayName}";
            if (!string.IsNullOrWhiteSpace(reply))
            {
                PStageTts.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
                await _voiceProvider.SpeakAsync(CurrentMemberProfile, reply);
                PStageTts.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
            }
        }
        catch (Exception ex)
        {
            AppendSystem($"{member.DisplayName}: (no reply — {ex.Message})");
            StatusText.Text = $"Chat failed: {ex.Message}";
            Log.Error(ex, "Fleet chat to {Member} failed", member.Name);
        }
    }

    private async void FleetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var member = SelectedMember;
            if (member is null) return;
            // Load the member's saved voice assignment into the dropdowns.
            if (SelectedMemberName is string name && _memberVoices.TryGetValue(name, out var saved))
            {
                foreach (var item in VoiceProviderSelect.Items)
                    if (item is ComboBoxItem pi && pi.Tag is VoiceProviderKind pk && pk == saved.Provider)
                    { VoiceProviderSelect.SelectedItem = pi; break; }
                await LoadVoicesForSelectedProviderAsync();
                foreach (var item in VoiceSelect.Items)
                    if (item is ComboBoxItem vi && vi.Tag?.ToString() == saved.Voice)
                    { VoiceSelect.SelectedItem = vi; break; }
            }
            StatusText.Text = $"Checking {member.DisplayName}…";
            var reachable = await _chat.IsReachableAsync(member);
            StatusText.Text = reachable
                ? $"{member.DisplayName} reachable"
                : $"{member.DisplayName} not reachable (no API key / not served)";
        }

    // ── PTT ──
    private async void TalkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_talking)
        {
            await _mic.StartAsync();
            _talking = true;
            TalkButton.Content = "■ Stop";
            PStageMic.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
            MicTag.Text = "🎤 On";
            MicTag.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
            StatusText.Text = "Listening…";
        }
        else
        {
            await _mic.StopAsync();
            _talking = false;
            TalkButton.Content = "🎤 Talk";
            PStageMic.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
            MicTag.Text = "🎤 Off";
            MicTag.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppRed"];
            StatusText.Text = "Ready";
        }
    }

    // ── Log / Error ──
    private void AddLog(string text)
        {
            if (LogPanel is null) return;
            LogPanel.Children.Add(new TextBlock
            {
                Text = $"{DateTime.Now:HH:mm:ss}  {text}",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"],
                FontSize = 11
            });
        }

    private void ShowError(string text)
    {
        ErrorBanner.Visibility = Visibility.Visible;
        ErrorBannerText.Text = text;
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
}
