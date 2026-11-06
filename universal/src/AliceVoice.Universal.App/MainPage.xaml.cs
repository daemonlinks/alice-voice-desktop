using AliceVoice.Core.Logging;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using AliceVoice.Universal.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.AppU;

public partial class MainPage : ContentPage
{
    private readonly VoiceSession _session;
    private readonly IServiceProvider _services;
    private readonly HashSet<string> _missingStyles = new(StringComparer.Ordinal);
    private IDispatcherTimer? _timer;
    private bool _started;
    private bool _rendering;
    private bool _sawPress;
    private bool _wakeServiceOn;

    public MainPage(VoiceSession session, IServiceProvider services)
    {
        _session = session;
        _services = services;
        InitializeComponent();
        HelpTriggers.Text = SessionCopy.HelpTriggers;
        HelpTools.Text = SessionCopy.HelpTools;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
#if ANDROID
        await Permissions.RequestAsync<Permissions.Microphone>();
        try { await Permissions.RequestAsync<Permissions.PostNotifications>(); } catch { /* older devices have no runtime notification permission */ }
#endif
        OperationTrail.Leave("ui.appear");
        WireSpeechStop();
        _session.Changed += () => MainThread.BeginInvokeOnMainThread(Render);
        _session.MeterChanged += () => MainThread.BeginInvokeOnMainThread(ApplyMeter);
        try { await _session.InitializeAsync(); }
        catch (Exception ex)
        {
            // The page already stayed open and showed the message. Keep that,
            // and also persist the full exception for the next launch.
            CrashDiagnostics.Report("startup", ex, "MainPage.InitializeAsync", fatal: false);
            await DisplayAlertAsync("Alice Voice", ex.Message, "OK");
        }
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += (_, _) => _session.OnUiTick();
        _timer.Start();
        Render();
    }

    private void WireSpeechStop()
    {
#if ANDROID
        if (_services.GetService<IPcmPlayer>() is { } player) _session.UseSpeechStop(player.Stop);
#elif WINDOWS
        if (_services.GetService<AliceVoice.Audio.ITtsPlayback>() is { } playback) _session.UseSpeechStop(playback.Stop);
#endif
    }

    private void Render()
    {
        if (ActivityLine is null) return;
        try { PaintSession(); }
        catch (Exception ex) { NotePaintFailure(ex); }
    }

    private void PaintSession()
    {
        _rendering = true;
        try
        {
            BrandBadge.Text = $"v{_session.Version} · yours";
            PresenceText.Text = _session.Connected ? "here with you" : "offline";
            PresenceDot.Color = Color.FromArgb(_session.Connected ? "#22C55E" : "#73737E");
            ActivityLine.Text = _session.Activity;
            ActivityDetail.Text = _session.ActivityDetail;
            ActivityDot.Color = Color.FromArgb(_session.Activity switch
            {
                "Listening…" or "Transcribing…" or "Thinking…" or "Speaking…" or "Connecting…" => "#EAB308",
                "Ready" => "#22C55E",
                _ => "#73737E"
            });
            StatusLine.Text = $"Agent: {_session.AgentLabel}   Backend: {_session.BackendLabel}   Provider: {_session.ProviderLabel}   Voice: {_session.VoiceLabel}   Connection: {_session.ConnectionLabel}   Pipeline: {Lamp(_session.MicLamp)} Mic  {Lamp(_session.SttLamp)} STT  {Lamp(_session.LlmLamp)} LLM  {Lamp(_session.TtsLamp)} TTS  {Lamp(_session.ApiLamp)} API  {_session.StageError}";
            StatusText.Text = _session.StatusText;
            UpdateBanner.IsVisible = !string.IsNullOrEmpty(_session.UpdateBanner);
            UpdateBanner.Text = _session.UpdateBanner ?? "";
            ErrorBanner.IsVisible = !string.IsNullOrEmpty(_session.Error);
            ErrorText.Text = _session.Error ?? "";
            ConnectButton.Text = _session.Connected ? "Disconnect" : "Connect";
            TalkButton.Text = _session.TalkButtonText;
            TalkButton.IsEnabled = _session.TalkEnabled;
            MicTag.Text = _session.MicTag;
            MicTag.TextColor = Color.FromArgb(_session.MicTagLamp == StageLamp.Green ? "#22C55E" : "#EF4444");
            WakeWordLabel.Text = _session.WakeWordLabel;
            SensitivityValue.Text = _session.SensitivityText;
            WakeScore.Text = _session.WakeScoreText;
            if (Math.Abs(WakeSensitivity.Value - _session.Sensitivity) > 0.001)
                WakeSensitivity.Value = _session.Sensitivity;
            ModeWake.IsEnabled = _session.WakeAvailable;
            TextRow.IsVisible = _session.Mode == "text";
            WakeRow.IsVisible = _session.Mode == "wake";
            PttRow.IsVisible = _session.Mode is "ptt" or "wake";
            SoulEditor.IsVisible = _session.Mode == "soul";
            TranscriptScroll.IsVisible = _session.Mode != "soul";
            if (_session.Mode == "soul" && SoulText.Text != _session.SoulText) SoulText.Text = _session.SoulText;
            SoulStatus.Text = _session.SoulStatus;
            Paint(ModePtt, _session.Mode == "ptt");
            Paint(ModeWake, _session.Mode == "wake");
            Paint(ModeText, _session.Mode == "text");
            Paint(ModeSoul, _session.Mode == "soul");
            Paint(MoodNatural, _session.Mood == "natural");
            Paint(MoodComfort, _session.Mood == "comfort");
            Paint(MoodPlayful, _session.Mood == "playful");
            Paint(MoodFlirty, _session.Mood == "flirty");
            Paint(MoodFocused, _session.Mood == "focused");
            var styleOn = _session.StyleEnabled;
            foreach (var b in new[] { MoodNatural, MoodComfort, MoodPlayful, MoodFlirty, MoodFocused, ProfQuick, ProfNatural, ProfFull })
                b.IsEnabled = styleOn;
            Paint(ProfQuick, _session.Profile == "quick");
            Paint(ProfNatural, _session.Profile == "natural");
            Paint(ProfFull, _session.Profile == "full");
            Paint(TtsAllTalk, _session.SelectedProvider == VoiceProviderKind.AllTalk);
            Paint(TtsPiper, _session.SelectedProvider == VoiceProviderKind.Piper);
            ProfHint.Text = _session.ProfileHint;
            TuningLast.Text = _session.Latency.LastTurnLine;
            SettingsModel.Text = _session.SettingsLine;
            FooterAuth.Text = _session.FooterAuth;
            LlmMode.Text = _session.LlmMode;
            WsDisplay.Text = _session.WsDisplay;
            MetricsLine.Text = $"E2E {_session.Latency.TotalText}   STT {_session.Latency.SttText}   LLM {_session.Latency.LlmText}   TTFT {_session.Latency.TtftText}   TTS {_session.Latency.TtsText}";
            MetricsSummary.Text = _session.Latency.Summary;
            MetricsTable.Text = _session.Latency.Table;
            FillPickers();
            FillTranscript();
            FillLog();
            ApplyBackground();
            SyncWakeService();
        }
        finally { _rendering = false; }
    }

    private void ApplyMeter()
    {
        try
        {
            if (VuFill is null) return;
            VuFill.WidthRequest = _session.VuWidth;
            WakeScore.Text = _session.WakeScoreText;
            MicTag.Text = _session.MicTag;
        }
        catch (Exception ex) { NotePaintFailure(ex); }
    }

    private void FillPickers()
    {
        if (AgentPicker.Items.Count == 0)
            foreach (var t in _session.Targets) AgentPicker.Items.Add(t.DisplayName);
        var agentIndex = 0;
        for (var i = 0; i < _session.Targets.Count; i++)
            if (_session.Targets[i].Id == _session.SelectedTargetId) agentIndex = i;
        if (AgentPicker.SelectedIndex != agentIndex) AgentPicker.SelectedIndex = agentIndex;

        var providers = _session.Providers.ToList();
        if (ProviderPicker.Items.Count != providers.Count)
        {
            ProviderPicker.Items.Clear();
            foreach (var p in providers) ProviderPicker.Items.Add(p.DisplayName);
        }
        var providerIndex = providers.FindIndex(p => p.Kind == _session.SelectedProvider);
        if (providerIndex >= 0 && ProviderPicker.SelectedIndex != providerIndex) ProviderPicker.SelectedIndex = providerIndex;

        if (VoicePicker.Items.Count != _session.VoiceNames.Count)
        {
            VoicePicker.Items.Clear();
            foreach (var v in _session.VoiceNames) VoicePicker.Items.Add(v.Replace(".wav", "").Replace("_", " "));
        }
        var voiceIndex = _session.VoiceNames.ToList().FindIndex(v => v == _session.SelectedVoice);
        if (voiceIndex >= 0 && VoicePicker.SelectedIndex != voiceIndex) VoicePicker.SelectedIndex = voiceIndex;
    }

    private void FillTranscript()
    {
        TranscriptPanel.Children.Clear();
        foreach (var entry in _session.Transcript)
        {
            var card = new VerticalStackLayout { Spacing = 2, Padding = 8, MaximumWidthRequest = 640 };
            card.Children.Add(new Label
            {
                Text = entry.Kind == TranscriptKind.System ? entry.Text : $"{entry.Speaker}  #{entry.Turn} · {entry.At:HH:mm:ss}",
                FontSize = 10,
                TextColor = Color.FromArgb("#73737E")
            });
            if (entry.Kind != TranscriptKind.System)
                card.Children.Add(new Label { Text = entry.Text, TextColor = Color.FromArgb("#E8E8ED"), LineBreakMode = LineBreakMode.WordWrap });
            if (entry.ShowRemember)
            {
                var remember = new Button { Text = entry.RememberLabel };
                // Chip lives in the application merged dictionary. The page
                // indexer does not search that scope and throws KeyNotFoundException.
                if (FindStyle("Chip") is { } chip) remember.Style = chip;
                remember.IsEnabled = entry.RememberEnabled;
                if (!entry.RememberEnabled && entry.RememberLabel == "♥ remember")
                    ToolTipProperties.SetText(remember, SessionCopy.RememberFleetTip);
                var captured = entry;
                remember.Clicked += async (_, _) => await _session.RememberAsync(captured);
                card.Children.Add(remember);
            }
            var align = entry.Kind switch
            {
                TranscriptKind.User => LayoutOptions.Start,
                TranscriptKind.Alice => LayoutOptions.End,
                _ => LayoutOptions.Center
            };
            TranscriptPanel.Children.Add(new Border
            {
                Content = card,
                Stroke = Color.FromArgb(entry.Kind == TranscriptKind.User ? "#66FF3B3B" : "#1F1F2A"),
                BackgroundColor = Color.FromArgb("#E6000000"),
                Padding = 4,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                HorizontalOptions = align
            });
        }
    }

    private void FillLog()
    {
        if (!LogPanel.IsVisible) return;
        LogPanel.Children.Clear();
        foreach (var line in _session.Logs.TakeLast(80))
        {
            var label = new Label { Text = line };
            if (FindStyle("Dim") is { } dim) label.Style = dim;
            LogPanel.Children.Add(label);
        }
    }

    private void ApplyBackground()
    {
        if (_session.BackgroundPath is { } path && File.Exists(path))
            BackgroundImageSource = ImageSource.FromFile(path);
    }

    private void SyncWakeService()
    {
        var on = _session.Mode == "wake" && _session.WakeAvailable;
        if (on == _wakeServiceOn) return;
        _wakeServiceOn = on;
#if ANDROID
        var ctx = Android.App.Application.Context;
        var intent = new Android.Content.Intent(ctx, typeof(WakeListeningService));
        if (on) ctx.StartForegroundService(intent);
        else ctx.StopService(intent);
#endif
    }

    private static string Lamp(StageLamp lamp) => lamp switch
    {
        StageLamp.Green => "🟢",
        StageLamp.Yellow => "🟡",
        StageLamp.Red => "🔴",
        StageLamp.Wait => "⏳",
        _ => "⚫"
    };

    private static void Paint(Button button, bool active)
    {
        button.TextColor = Color.FromArgb(active ? "#FF3B3B" : "#73737E");
        button.BorderColor = Color.FromArgb(active ? "#FF3B3B" : "#1F1F2A");
    }

    private async void OnConnect(object? sender, EventArgs e)
    {
        if (_session.Connected) _session.Disconnect();
        else await GuardUi(nameof(OnConnect), () => _session.ConnectAsync(force: true));
    }

    private async void OnReset(object? sender, EventArgs e) => await GuardUi(nameof(OnReset), () => _session.ResetAsync());

    private async void OnAgentChanged(object? sender, EventArgs e)
    {
        if (_rendering || AgentPicker.SelectedIndex < 0 || AgentPicker.SelectedIndex >= _session.Targets.Count) return;
        await GuardUi(nameof(OnAgentChanged), () => _session.SelectTargetAsync(_session.Targets[AgentPicker.SelectedIndex].Id));
    }

    private async void OnProviderChanged(object? sender, EventArgs e)
    {
        if (_rendering || ProviderPicker.SelectedIndex < 0) return;
        var providers = _session.Providers.ToList();
        if (ProviderPicker.SelectedIndex >= providers.Count) return;
        await GuardUi(nameof(OnProviderChanged), () => _session.SetProviderAsync(providers[ProviderPicker.SelectedIndex].Kind));
    }

    private void OnVoiceChanged(object? sender, EventArgs e)
    {
        if (_rendering || VoicePicker.SelectedIndex < 0 || VoicePicker.SelectedIndex >= _session.VoiceNames.Count) return;
        _session.SetVoice(_session.VoiceNames[VoicePicker.SelectedIndex]);
    }

    private async void OnTestTts(object? sender, EventArgs e) => await GuardUi(nameof(OnTestTts), () => _session.TestVoiceAsync());
    private void OnMode(object? sender, EventArgs e) => _session.SetMode(((Button)sender!).Text!.ToLowerInvariant());
    private void OnMood(object? sender, EventArgs e) => _session.SetMood(((Button)sender!).Text!);
    private void OnProfile(object? sender, EventArgs e) => _session.SetProfile(((Button)sender!).Text!.ToLowerInvariant());

    private async void OnEngine(object? sender, EventArgs e)
    {
        var kind = sender == TtsPiper ? VoiceProviderKind.Piper : VoiceProviderKind.AllTalk;
        await GuardUi(nameof(OnEngine), () => _session.SetProviderAsync(kind));
    }

    private void OnSensitivity(object? sender, ValueChangedEventArgs e)
    {
        if (_rendering) return;
        _session.SetSensitivity(e.NewValue);
    }

    private async void OnTalkPressed(object? sender, EventArgs e) { _sawPress = true; await GuardUi(nameof(OnTalkPressed), () => _session.PressTalkAsync()); }
    private async void OnTalkReleased(object? sender, EventArgs e) => await GuardUi(nameof(OnTalkReleased), () => _session.ReleaseTalkAsync());
    private async void OnTalkClicked(object? sender, EventArgs e)
    {
        if (_sawPress) { _sawPress = false; return; }
        await GuardUi(nameof(OnTalkClicked), () => _session.ToggleTalkAsync());
    }

    private async void OnSend(object? sender, EventArgs e)
    {
        var text = ChatInput.Text;
        if (string.IsNullOrWhiteSpace(text) || !_session.Connected)
            return;
        ChatInput.Text = "";
        await GuardUi(nameof(OnSend), () => _session.SendTextAsync(text));
    }

    private async void OnSaveSoul(object? sender, EventArgs e) => await GuardUi(nameof(OnSaveSoul), () => _session.SaveSoulAsync(SoulText.Text ?? ""));
    private void OnCloseSoul(object? sender, EventArgs e) => _session.SetMode("ptt");
    private void OnDismissError(object? sender, EventArgs e) => _session.DismissError();
    private void OnPrevBg(object? sender, EventArgs e) => _session.StepBackground(-1);
    private void OnNextBg(object? sender, EventArgs e) => _session.StepBackground(1);
    private void OnRandBg(object? sender, EventArgs e) => _session.RandomBackground();
    private void OnToggleLog(object? sender, EventArgs e) { LogPanel.IsVisible = !LogPanel.IsVisible; LogToggle.Text = (LogPanel.IsVisible ? "▾" : "▸") + "  Event Log"; FillLog(); }
    private void OnToggleHelp(object? sender, EventArgs e) => HelpPanel.IsVisible = !HelpPanel.IsVisible;
    private void OnToggleSettings(object? sender, EventArgs e) => SettingsPanel.IsVisible = !SettingsPanel.IsVisible;
    private void OnToggleMetrics(object? sender, EventArgs e) => MetricsPanel.IsVisible = !MetricsPanel.IsVisible;
    private void OnToggleTuning(object? sender, EventArgs e) => TuningPanel.IsVisible = !TuningPanel.IsVisible;

    /// <summary>
    /// Page.Resources does not include styles merged on the application.
    /// StaticResource in XAML walks that scope; the dictionary indexer does not.
    /// </summary>
    private Style? FindStyle(string key)
    {
        var scopes = new List<ResourceDictionary>();
        if (Resources is not null) scopes.Add(Resources);
        if (Application.Current?.Resources is { } application) scopes.Add(application);
        if (ResourceLookup.TryFind(scopes, key, ReadLocal, static dictionary => dictionary.MergedDictionaries, out var value) && value is Style style)
            return style;
        if (_missingStyles.Add(key)) OperationTrail.Leave("ui.style.missing", key);
        return null;
    }

    private static object? ReadLocal(ResourceDictionary dictionary, string key)
        => dictionary.TryGetValue(key, out var value) ? value : null;

    private async Task GuardUi(string source, Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex)
        {
            // Caught here so an async void handler cannot become a fatal WinUI
            // exception. The fatal hooks still apply to anything this does not catch.
            try { CrashDiagnostics.Report("ui", ex, "MainPage." + source, fatal: false); }
            catch { /* the session error, if set, is already stored */ }
        }
    }

    private void NotePaintFailure(Exception ex)
    {
        OperationTrail.Leave("ui.paint.failed", ex.GetType().Name);
        try { CrashDiagnostics.Report("ui", ex, "MainPage.Render", fatal: false); }
        catch { /* keep the process up; the session still holds the original error */ }
        try
        {
            if (ErrorBanner is null || ErrorText is null || string.IsNullOrEmpty(_session.Error)) return;
            ErrorBanner.IsVisible = true;
            ErrorText.Text = _session.Error;
        }
        catch { /* banner paint is best-effort after the session error is stored */ }
    }
}
