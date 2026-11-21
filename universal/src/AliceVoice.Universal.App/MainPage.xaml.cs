using AliceVoice.Core.Logging;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Media;
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
    private int _overlayVersion = -1;
    private string? _logShown;
    private readonly List<Button> _backendChips = new();
    private bool _backendChipsBuilt;

    public MainPage(VoiceSession session, IServiceProvider services)
    {
        _session = session;
        _services = services;
        InitializeComponent();
        HelpTriggers.Text = SessionCopy.HelpTriggers;
        HelpTools.Text = SessionCopy.HelpTools;
        HelpHoncho.Text = SessionCopy.HelpHoncho;
        // AspectFit is also set in XAML. Restated here so a fill/crop mode is not introduced in code.
        SceneImage.Aspect = Aspect.AspectFit;
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
            VersionLine.Text = $"App U v{_session.Version}";
            ApplyLamps();
            ActivityLine.Text = _session.Activity;
            var stageNote = string.IsNullOrEmpty(_session.StageError) ? "" : _session.StageError;
            ActivityDetail.Text = string.IsNullOrEmpty(stageNote)
                ? _session.ActivityDetail
                : string.IsNullOrEmpty(_session.ActivityDetail) ? stageNote : _session.ActivityDetail + " · " + stageNote;
            AgentLine.Text = $"Agent {_session.AgentLabel} · Voice {_session.ProviderLabel} · {_session.VoiceLabel}";
            StatusText.Text = _session.StatusText;
            UpdateBanner.IsVisible = !string.IsNullOrEmpty(_session.UpdateBanner);
            UpdateBanner.Text = _session.UpdateBanner ?? "";
            ErrorBanner.IsVisible = !string.IsNullOrEmpty(_session.Error);
            ErrorText.Text = _session.Error ?? "";
            ConnectButton.Text = _session.Connected ? "Disconnect" : "Connect";
            TalkButton.Text = _session.TalkButtonText;
            TalkButton.IsEnabled = _session.TalkEnabled;
            MicTag.Text = _session.MicTag;
            MicTag.TextColor = Tone(StatusTones.Mic(_session.MicLamp, _session.MicTagLamp, _session.Activity));
            WakeWordLabel.Text = _session.WakeWordLabel;
            SensitivityValue.Text = _session.SensitivityText;
            WakeScore.Text = _session.WakeScoreText;
            if (Math.Abs(WakeSensitivity.Value - _session.Sensitivity) > 0.001)
                WakeSensitivity.Value = _session.Sensitivity;
            ModeWake.IsEnabled = _session.WakeAvailable;
            TextRow.IsVisible = _session.Mode == "text";
            WakeRow.IsVisible = _session.Mode == "wake";
            TalkButton.IsVisible = _session.Mode is "ptt" or "wake";
            MeterRow.IsVisible = _session.Mode is "ptt" or "wake";
            SoulEditor.IsVisible = _session.Mode == "soul";
            TranscriptScroll.IsVisible = _session.Mode != "soul";
            if (_session.Mode == "soul" && SoulText.Text != _session.SoulText) SoulText.Text = _session.SoulText;
            SoulStatus.Text = _session.SoulStatus;
            SoulStatus.TextColor = Tone(StatusTones.ForNote(_session.SoulKind));
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
            // Mood and length only tune Alice LOCAL; hide them where they do nothing.
            StyleSection.IsVisible = styleOn;
            Paint(ProfQuick, _session.Profile == "quick");
            Paint(ProfNatural, _session.Profile == "natural");
            Paint(ProfFull, _session.Profile == "full");
            Paint(TtsAllTalk, _session.SelectedProvider == VoiceProviderKind.AllTalk);
            Paint(TtsPiper, _session.SelectedProvider == VoiceProviderKind.Piper);
            ProfHint.Text = _session.ProfileHint;
            TuningLast.Text = _session.Latency.LastTurnLine;
            SettingsModel.Text = _session.SettingsLine;
            BridgeEditor.IsVisible = _session.ShowBridgeEditor;
            FooterAuth.Text = _session.FooterAuth;
            MetricsLine.Text = $"E2E {_session.Latency.TotalText}   STT {_session.Latency.SttText}   LLM {_session.Latency.LlmText}   TTFT {_session.Latency.TtftText}   TTS {_session.Latency.TtsText}";
            MetricsSummary.Text = _session.Latency.Summary;
            MetricsTable.Text = _session.Latency.Table;
            FillPickers();
            FillBackendChips();
            PaintDock();
            FillTranscript();
            FillLog();
            ApplyBackground();
            ApplyOverlay();
            MediaLine.Text = MediaSummary(_session.Images.Settings);
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
            MicTag.TextColor = Tone(StatusTones.Mic(_session.MicLamp, _session.MicTagLamp, _session.Activity));
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

    // Backend chips: Alice LOCAL on Ollama, then every fleet member. Any fleet member can
    // be the backend. This is the same choice the Agent picker offers, one click away in
    // the top bar, and it shows which backend is live.
    private void FillBackendChips()
    {
        if (!_backendChipsBuilt)
        {
            _backendChipsBuilt = true;
            foreach (var target in _session.Targets)
            {
                var chip = new Button { Text = target.DisplayName, Margin = new Thickness(0, 0, 6, 4) };
                if (FindStyle("Chip") is { } chipStyle) chip.Style = chipStyle;
                ToolTipProperties.SetText(chip, target.Id == VoiceSession.AliceLocalId
                    ? "Alice LOCAL — the local Ollama brain, wherever it runs"
                    : $"Fleet member {target.Id} — a Hermes agent backend");
                var id = target.Id;
                chip.Clicked += async (_, _) => await GuardUi(nameof(FillBackendChips), () => _session.SelectTargetAsync(id));
                _backendChips.Add(chip);
                BackendChipRow.Children.Add(chip);
            }
        }
        for (var i = 0; i < _backendChips.Count && i < _session.Targets.Count; i++)
            Paint(_backendChips[i], _session.Targets[i].Id == _session.SelectedTargetId);
    }

    // The dock carries the same three facts the web page's dock carries: what is happening
    // now (amber while busy, red on an error), the route of the turn, and its timings.
    // Space for it is reserved by the layout, so the log is never overlaid on the talk.
    private void PaintDock()
    {
        var busy = !string.Equals(_session.Activity, "Ready", StringComparison.Ordinal);
        var failed = !string.IsNullOrEmpty(_session.Error) || !string.IsNullOrEmpty(_session.StageError);
        DockNow.Text = failed
            ? "error · " + (_session.Error ?? _session.StageError)
            : busy
                ? (string.IsNullOrEmpty(_session.ActivityDetail) ? _session.Activity : _session.Activity + " · " + _session.ActivityDetail)
                : _session.Connected ? "idle · connected to " + _session.TargetName : "idle · not connected";
        DockNow.TextColor = failed ? Tone(StatusTone.Red) : busy ? Tone(StatusTone.Amber) : Color.FromArgb("#73737E");
        DockRoute.Text = $"route · {_session.TargetName} · {_session.BackendLabel} · {_session.ProviderLabel} / {_session.VoiceLabel}";
        DockTimings.Text = "timings · " + _session.Latency.LastTurnLine;
    }

    private void FillTranscript()
    {
        TranscriptPanel.Children.Clear();
        foreach (var entry in _session.Transcript)
        {
            var card = new VerticalStackLayout { Spacing = 2, Padding = 8, MaximumWidthRequest = 640 };
            if (entry.Kind == TranscriptKind.System)
            {
                card.Children.Add(MessageText(entry.Text, 10, "#73737E"));
            }
            else
            {
                card.Children.Add(new Label
                {
                    Text = $"{entry.Speaker}  #{entry.Turn} · {entry.At:HH:mm:ss}",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#73737E")
                });
                card.Children.Add(MessageText(entry.Text, 13, "#E8E8ED"));
            }
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

    /// <summary>
    /// Message text, not the speaker line. SelectableLabel is what makes a
    /// drag highlight the words. A plain Label cannot.
    /// </summary>
    private static SelectableLabel MessageText(string text, int fontSize, string color) => new SelectableLabel
    {
        Text = text,
        FontSize = fontSize,
        TextColor = Color.FromArgb(color),
        LineBreakMode = LineBreakMode.WordWrap
    };

    /// <summary>The bottom log: one selectable label, repainted only when a line arrives.</summary>
    private void FillLog()
    {
        var logs = _session.Logs;
        var tail = string.Join(Environment.NewLine, logs.Skip(Math.Max(0, logs.Count - 200)));
        if (tail == _logShown) return;
        _logShown = tail;
        // Style comes from the application dictionary; the page indexer cannot see it.
        if (LogLabel.Style is null && FindStyle("Dim") is { } dim) LogLabel.Style = dim;
        LogLabel.Text = tail;
        _ = LogScroll.ScrollToAsync(0, double.MaxValue, false);
    }

    private async void OnCopyLog(object? sender, EventArgs e) => await GuardUi(nameof(OnCopyLog), async () =>
    {
        var text = _session.LogText;
        await Clipboard.Default.SetTextAsync(text);
        var lines = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
        CopyStatus.Text = $"Copied {lines} lines";
    });

    /// <summary>
    /// Paints the agent's picture. Stage sits centred over a dimmed page, card sits in a
    /// corner and leaves the conversation usable, backdrop takes the scene's place.
    /// Rendering is logged locally only; nothing is sent back to the agent.
    /// </summary>
    private void ApplyOverlay()
    {
        var state = _session.Images.State;
        if (state.Version == _overlayVersion) return;
        _overlayVersion = state.Version;
        var current = state.Current;
        StageLayer.IsVisible = current?.Mode == OverlayMode.Stage;
        CardLayer.IsVisible = current?.Mode == OverlayMode.Card;
        if (current is not null)
        {
            var image = current.Mode == OverlayMode.Stage ? StageImage : CardImage;
            var caption = current.Mode == OverlayMode.Stage ? StageCaption : CardCaption;
            var bytes = current.Bytes;
            image.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            caption.Text = current.Caption;
            caption.IsVisible = !string.IsNullOrEmpty(current.Caption);
            _session.Images.NoteRendered(current.Id, ok: true);
        }
        else
        {
            StageImage.Source = null;
            CardImage.Source = null;
        }
        if (state.Backdrop is { } backdrop)
        {
            var bytes = backdrop.Bytes;
            BackdropImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            BackdropImage.IsVisible = true;
            SceneImage.IsVisible = false;
            _session.Images.NoteRendered(backdrop.Id, ok: true);
        }
        else
        {
            BackdropImage.Source = null;
            BackdropImage.IsVisible = false;
        }
    }

    private static string MediaSummary(MediaSettings s) =>
        !s.Enabled ? "Agent pictures: off"
        : $"Agent pictures: on · {(s.AllowHttps ? "allowed folders + https" : "allowed folders only")} · up to {s.MaxBytes / (1024 * 1024)} MB · {s.DefaultDurationS:0.#} s when not kept";

    private void OnClosePicture(object? sender, EventArgs e) => _session.Images.CloseByUser();

    private void OnToggleOptions(object? sender, EventArgs e)
    {
        OptionsPanel.IsVisible = !OptionsPanel.IsVisible;
        OptionsToggle.Text = OptionsPanel.IsVisible ? "Options ▾" : "Options ▸";
    }

    private void ApplyLamps()
    {
        var presence = StatusTones.Presence(_session.Connected, _session.Activity);
        PresenceDot.Color = Tone(presence);
        PresenceText.Text = presence switch
        {
            StatusTone.Amber => "Connecting",
            StatusTone.Red => "Offline",
            _ => "Connected"
        };
        PresenceText.TextColor = Tone(presence);

        var activity = StatusTones.Activity(_session.Activity, _session.Connected);
        ActivityDot.Color = Tone(activity);

        var micDetail = _session.MicTag.Replace("🎤 ", "", StringComparison.Ordinal).Trim();
        PaintStage(MicDot, MicLampText, "Mic", StatusTones.Mic(_session.MicLamp, _session.MicTagLamp, _session.Activity), micDetail);
    }

    private static void PaintStage(BoxView dot, Label label, string name, StatusTone tone, string? detail)
    {
        dot.Color = Tone(tone);
        label.Text = string.IsNullOrEmpty(detail) ? StatusTones.Caption(name, tone) : name + " " + detail;
        label.TextColor = tone == StatusTone.Off ? Color.FromArgb("#73737E") : Tone(tone);
    }

    private static Color Tone(StatusTone tone) => Color.FromArgb(StatusTones.Hex(tone));

    private void ApplyBackground()
    {
        if (_session.BackgroundPath is { } path && File.Exists(path))
        {
            SceneImage.IsVisible = _session.Images.State.Backdrop is null;
            SceneImage.Aspect = Aspect.AspectFit;
            if (SceneImage.Source is FileImageSource current && current.File == path) return;
            SceneImage.Source = ImageSource.FromFile(path);
            return;
        }
        SceneImage.Source = null;
        SceneImage.IsVisible = false;
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
    private void OnToggleHelp(object? sender, EventArgs e)
    {
        HelpPanel.IsVisible = !HelpPanel.IsVisible;
        HelpToggle.Text = (HelpPanel.IsVisible ? "▾" : "▸") + "  Keywords & Tools";
    }
    private void OnToggleSettings(object? sender, EventArgs e)
    {
        SettingsPanel.IsVisible = !SettingsPanel.IsVisible;
        SettingsToggle.Text = (SettingsPanel.IsVisible ? "▾" : "▸") + "  Settings";
        if (!SettingsPanel.IsVisible) return;
        BeastHostEntry.Text = _session.BridgeHost;
        BeastPortEntry.Text = _session.BridgePort;
        BridgeSaveStatus.Text = _session.BridgeStatus;
    }

    private void OnSaveBridge(object? sender, EventArgs e)
    {
        var error = _session.TrySaveBridge(BeastHostEntry.Text ?? "", BeastPortEntry.Text ?? "");
        BridgeSaveStatus.Text = error ?? _session.BridgeStatus;
    }
    private void OnToggleMetrics(object? sender, EventArgs e)
    {
        MetricsPanel.IsVisible = !MetricsPanel.IsVisible;
        MetricsToggle.Text = (MetricsPanel.IsVisible ? "▾" : "▸") + "  Latency";
    }

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
