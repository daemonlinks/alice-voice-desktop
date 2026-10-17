using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Updates;
using AliceVoice.Inference;

namespace AliceVoice.App;

public sealed partial class MainWindow : Window
{
    private readonly IGpuSelector _gpu;
    private readonly IWhisperEngine _whisper;
    private readonly IAppUpdater _updater;

    public MainWindow()
    {
        InitializeComponent();

        _gpu = App.Services.GetRequiredService<IGpuSelector>();
        _whisper = App.Services.GetRequiredService<IWhisperEngine>();
        _updater = App.Services.GetRequiredService<IAppUpdater>();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var gpu = _gpu.PreferredInferenceGpu;
            GpuText.Text = $"GPU: {gpu.Name} (idx {gpu.Index}, {gpu.TotalMemoryMiB} MiB)";
            EngineText.Text = "Engine: ONNX Runtime + DirectML";
            StatusText.Text = "Ready";

            // Warm the Whisper model on the inference GPU.
            await _whisper.WarmUpAsync();
            StatusText.Text = "Ready — Whisper loaded";
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

    private void TalkButton_Click(object sender, RoutedEventArgs e)
    {
        // Placeholder — WASAPI capture + Whisper transcription wired in next.
        StatusText.Text = "Talk wired in next step";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Settings coming";
    }
}
