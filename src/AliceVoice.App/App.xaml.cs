using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AliceVoice.Core;
using AliceVoice.Core.Logging;
using AliceVoice.Inference;
using AliceVoice.Audio;

namespace AliceVoice.App;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Structured logging: Event Log + rolling file, best-practice setup.
        LoggingBootstrap.Configure();

        // Dependency injection container wiring the whole app.
        Services = new ServiceCollection()
            .AddCoreServices()
            .AddInferenceServices()
            .AddAudioServices()
            .BuildServiceProvider();

        _window = new MainWindow();
        _window.Activate();
    }
}
