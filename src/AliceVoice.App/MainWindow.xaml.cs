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
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var gpu = _gpu.PreferredInferenceGpu;
            GpuText.Text = $"GPU: {gpu.Name} (idx {gpu.Index}, {gpu.TotalMemoryMiB} MiB)";
            EngineText.Text = "Engine: ONNX Runtime + DirectML";
            StatusText.Text = "Connecting…";

            // Warm the Whisper model on the inference GPU (local fallback path).
            await _whisper.WarmUpAsync();

            // Start on the relay backend (default state).
            await _voice.SwitchToAsync(VoiceBackendKind.Relay);
            StatusText.Text = "Ready — relay backend";
            Log.Information("Alice Voice started. Inference GPU: {Gpu}", gpu.Name);

            // Check for updates in the background (non-blocking).
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error";
            Log.Error(ex, "Failed to initialise Alice Voice");
        }
    }

    private void OnVoiceEvent(BackendEvent evt)
    {
        // Marshal to UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (evt.Type)
            {
                case "transcript":
                    AppendTranscript($"You: {evt.Text}");
                    break;
                case "reply_text":
                    AppendTranscript($"Alice: {evt.Text}");
                    break;
                case "tts_start":
                    StatusText.Text = "Speaking…";
                    break;
                case "tts_end":
                    StatusText.Text = "Ready";
                    break;
                case "wake_detected":
                    StatusText.Text = "Wake word heard — listening";
                    break;
                case "backend_switched":
                    StatusText.Text = evt.Text;
                    break;
                case "error":
                    StatusText.Text = $"Error: {evt.Text}";
                    break;
            }
        });
    }

    private void OnVoiceAudio(byte[] pcm)
    {
        // Relay streams raw Int16 PCM (22050 Hz AllTalk / 16000 Hz Edge).
        _tts.EnqueuePcm(pcm, 22050);
    }

    private void OnMicFrame(float[] pcm16k)
    {
        // Convert float 16k mono to Int16 and send to the current backend for STT.
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
        TranscriptPanel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null);
    }

    private async void BackendSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackendSelector.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;

        var kind = tag switch
        {
            "relay" => VoiceBackendKind.Relay,
            "agent" => VoiceBackendKind.AliceVoiceAgent,
            "ollama" => VoiceBackendKind.LocalOllama,
            _ => VoiceBackendKind.Relay
        };

        try
        {
            StatusText.Text = $"Switching to {kind}…";
            await _voice.SwitchToAsync(kind);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Switch failed: {ex.Message}";
            Log.Error(ex, "Backend switch to {Kind} failed", kind);
        }
    }

    private async void TalkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_talking)
        {
            await _mic.StartAsync();
            _talking = true;
            TalkButton.Content = "Release to Stop";
            StatusText.Text = "Listening…";
        }
        else
        {
            await _mic.StopAsync();
            _talking = false;
            TalkButton.Content = "Hold to Talk";
            StatusText.Text = "Ready";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Settings coming";
    }

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
