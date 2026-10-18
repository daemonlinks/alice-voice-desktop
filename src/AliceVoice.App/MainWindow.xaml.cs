using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Audio;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using AliceVoice.Inference;

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

    private bool _talking;
    private string _facet = "alice";
    private string _mood = "natural";
    private string _mode = "ptt";

    public MainWindow()
    {
        InitializeComponent();

        _gpu = App.Services.GetRequiredService<IGpuSelector>();
        _whisper = App.Services.GetRequiredService<IWhisperEngine>();
        _updater = App.Services.GetRequiredService<IAppUpdater>();
        _voice = App.Services.GetRequiredService<IVoiceBackendManager>();
        _mic = App.Services.GetRequiredService<IMicrophoneCapture>();
        _tts = App.Services.GetRequiredService<ITtsPlayback>();
        _chat = App.Services.GetRequiredService<IFleetChat>();

        _voice.EventReceived += OnVoiceEvent;
        _voice.AudioReceived += OnVoiceAudio;
        _mic.AudioFrameCaptured += OnMicFrame;

        // Populate the fleet selector.
        foreach (var m in FleetRoster.Members)
            FleetSelector.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.Name });
        FleetSelector.SelectedIndex = 0;

        // Populate voice select (Edge TTS voices).
        foreach (var v in new[] { "en-GB-SoniaNeural", "en-GB-LibbyNeural", "en-US-AriaNeural", "en-US-JennyNeural" })
            VoiceSelect.Items.Add(v);
        VoiceSelect.SelectedIndex = 0;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Warm the Whisper model (local fallback path — non-fatal).
            await _whisper.WarmUpAsync();

            // Start on the alice_voice agent backend (default state).
            await _voice.SwitchToAsync(VoiceBackendKind.AliceVoiceAgent);
            SetConnected(true);
            Log.Information("Alice Voice started");

            // Check for updates in the background (non-blocking).
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error";
            Log.Error(ex, "Failed to initialise Alice Voice");
        }
    }

    private void SetConnected(bool connected)
    {
        ConnDot.Fill = connected
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
        ConnText.Text = connected ? "Connected" : "Connect";
        PresenceText.Text = connected ? "here with you" : "offline";
    }

    // ── Facet (Alice / Plum) ──
    private void FacetAlice_Click(object sender, RoutedEventArgs e)
    {
        _facet = "alice";
        FacetAliceBtn.Style = (Style)Application.Current.Resources["ChipButtonActive"];
        FacetPlumBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        AppendTranscript("(facet: Alice)");
    }

    private void FacetPlum_Click(object sender, RoutedEventArgs e)
    {
        _facet = "plum";
        FacetPlumBtn.Style = (Style)Application.Current.Resources["ChipButtonActive"];
        FacetAliceBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        AppendTranscript("(facet: Plum)");
    }

    // ── Modes ──
    private void SetMode(string mode)
    {
        _mode = mode;
        ModePttBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        ModeWakeBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        ModeTextBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        ModeSoulBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        var active = mode switch
        {
            "ptt" => ModePttBtn, "wake" => ModeWakeBtn,
            "text" => ModeTextBtn, "soul" => ModeSoulBtn, _ => ModePttBtn
        };
        active.Style = (Style)Application.Current.Resources["ChipButtonActive"];
        StatusText.Text = $"Mode: {mode}";
    }

    private void ModePtt_Click(object sender, RoutedEventArgs e) => SetMode("ptt");
    private void ModeWake_Click(object sender, RoutedEventArgs e) => SetMode("wake");
    private void ModeText_Click(object sender, RoutedEventArgs e) => SetMode("text");
    private void ModeSoul_Click(object sender, RoutedEventArgs e) => SetMode("soul");

    // ── Mood ──
    private void SetMood(string mood)
    {
        _mood = mood;
        MoodNaturalBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        MoodComfortBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        MoodPlayfulBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        MoodFlirtyBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        MoodFocusedBtn.Style = (Style)Application.Current.Resources["ChipButton"];
        var active = mood switch
        {
            "natural" => MoodNaturalBtn, "comfort" => MoodComfortBtn,
            "playful" => MoodPlayfulBtn, "flirty" => MoodFlirtyBtn,
            "focused" => MoodFocusedBtn, _ => MoodNaturalBtn
        };
        active.Style = (Style)Application.Current.Resources["ChipButtonActive"];
    }

    private void MoodNatural_Click(object sender, RoutedEventArgs e) => SetMood("natural");
    private void MoodComfort_Click(object sender, RoutedEventArgs e) => SetMood("comfort");
    private void MoodPlayful_Click(object sender, RoutedEventArgs e) => SetMood("playful");
    private void MoodFlirty_Click(object sender, RoutedEventArgs e) => SetMood("flirty");
    private void MoodFocused_Click(object sender, RoutedEventArgs e) => SetMood("focused");

    // ── Connect / Test TTS ──
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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

    private void TestTts_Click(object sender, RoutedEventArgs e)
    {
        AppendTranscript("(TTS test)");
        StatusText.Text = "TTS test sent";
    }

    // ── Voice events ──
    private void OnVoiceEvent(BackendEvent evt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (evt.Type)
            {
                case "transcript":
                    AppendTranscript($"You: {evt.Text}");
                    PStageStt.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppGreen"];
                    break;
                case "reply_text":
                    AppendTranscript($"{_facet}: {evt.Text}");
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

    private void AppendTranscript(string line)
    {
        TranscriptPanel.Children.Add(new TextBlock
        {
            Text = line,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppText"]
        });
        TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
    }

    // ── Fleet chat ──
    private FleetMember? SelectedMember =>
        FleetSelector.SelectedItem is ComboBoxItem item && item.Tag is string name
            ? FleetRoster.Find(name)
            : null;

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
        AppendTranscript($"You → {member.DisplayName}: {text}");
        StatusText.Text = $"Talking to {member.DisplayName}…";

        try
        {
            var reply = await _chat.SendAsync(member, text);
            AppendTranscript($"{member.DisplayName}: {reply}");
            StatusText.Text = $"Replied from {member.DisplayName}";
        }
        catch (Exception ex)
        {
            AppendTranscript($"{member.DisplayName}: (no reply — {ex.Message})");
            StatusText.Text = $"Chat failed: {ex.Message}";
            Log.Error(ex, "Fleet chat to {Member} failed", member.Name);
        }
    }

    private async void FleetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var member = SelectedMember;
        if (member is null) return;

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
            StatusText.Text = "Listening…";
        }
        else
        {
            await _mic.StopAsync();
            _talking = false;
            TalkButton.Content = "🎤 Talk";
            PStageMic.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppTextDim"];
            StatusText.Text = "Ready";
        }
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
