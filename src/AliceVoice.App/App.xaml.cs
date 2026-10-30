using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Core;
using AliceVoice.Core.Config;
using AliceVoice.Core.Logging;
using AliceVoice.Audio;

namespace AliceVoice.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    private Window? _window;
    private AliceVoiceOptions? _options;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrash("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _options = AliceVoiceOptions.Load();

        // Structured logging: rolling file + console (+ Event Log when available).
        LoggingBootstrap.Configure(_options.LogsDir);

        // Dependency injection container wiring the whole app. The experimental
        // ONNX/DirectML engine (AliceVoice.Inference) is intentionally not
        // registered: faster-whisper on GPU 1 does STT, and DirectML adapter
        // ordinals do not reliably map to nvidia-smi indices (could hit GPU 0).
        Services = new ServiceCollection()
            .AddCoreServices(_options)
            .AddAudioServices()
            .BuildServiceProvider();

        _window = new MainWindow();
        _window.Closed += async (_, _) =>
        {
            try { await Services.DisposeAsync(); } // stops the warm STT process, mic, playback
            catch (Exception ex) { Log.Warning(ex, "Service shutdown failed"); }
            finally { await Log.CloseAndFlushAsync(); }
        };
        _window.Activate();
    }

    private void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            Log.Fatal(ex, "Unhandled {Kind} exception", kind);
            var dir = (_options ?? AliceVoiceOptions.Load()).CrashDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.txt"),
                $"{DateTime.Now:O}\n{kind}\n{ex}");
            Log.CloseAndFlush();
        }
        catch { /* never throw from a crash handler */ }
    }
}
