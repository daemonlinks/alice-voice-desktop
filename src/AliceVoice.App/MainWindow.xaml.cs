using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Audio;
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
    private readonly IVoiceClient _voice;
    private readonly IMicrophoneCapture _mic;
    private readonly ITtsPlayback _tts;

    private bool _talking;

    public MainWindow()
    {
        InitializeComponent();

        _gpu = App.Services.GetRequiredService<IGpuSelector>();
        _whisper = App.Services.GetRequiredService<IWhisperEngine>();
        _updater = App.Services.GetRequiredService<IAppUpdater>();
        _voice = App.Services.GetRequiredService<IVoiceClient>();
        _mic = App.Services.GetRequiredService<IMicrophoneCapture>();
        _tts = App.Services.GetRequiredService<ITtsPlayback>();

        _voice.EventReceived += OnVoiceEvent;
        _voice.AudioReceived += OnVoiceAudio;
        _mic.AudioFrameCaptured += OnMicFrame;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var gpu = _gpu.PreferredInferenceGpu;
            GpuText.Text = $"GPU: {gpu.Name} (idx {gpu.Index}, {gpu.TotalMemoryMiB} MiB)";
            EngineText.Text = "Engine: ONNX Runtime + DirectML";
            StatusText.Text = "Connecting to relay…";

            // Warm the Whisper model on the inference GPU (local fallback path).
            await _whisper.WarmUpAsync();

            // Connect to the Alice Voice relay (reuses STT/TTS/LLM/Honcho backend).
            await _voice.ConnectAsync(new Uri("ws://localhost:9876"));
            StatusText.Text = "Ready — connected to relay";
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

    private void OnVoiceEvent(VoiceEvent evt)
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
        // Convert float 16k mono to Int16 and send to relay for STT.
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
