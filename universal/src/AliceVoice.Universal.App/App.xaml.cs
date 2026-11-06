using AliceVoice.Core.Logging;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AliceVoice.AppU;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        // Idempotent. The platform entry point and MauiProgram install first;
        // this covers a head whose entry point did not.
        CrashDiagnostics.Install();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_services.GetRequiredService<MainPage>())
        {
            Title = "Alice Voice App U"
        };
#if WINDOWS
        window.Width = 1200;
        window.Height = 800;
#endif
        window.Created += async (_, _) => await StartBridgeAsync();
        window.Destroying += async (_, _) => await ShutdownAsync();
        OperationTrail.Leave("startup.window");
        return window;
    }

    private async Task StartBridgeAsync()
    {
#if WINDOWS
        if (_services.GetService<BeastBridge>() is not { } bridge) return;
        OperationTrail.Leave("bridge.start");
        try { await bridge.StartAsync(); }
        catch (Exception ex)
        {
            // Caught on purpose: a refused bind must not take down the window.
            // The exception is still written to the failure file and the log.
            CrashDiagnostics.Report("startup", ex, "BeastBridge.StartAsync. A non-loopback bind on Windows needs a URL reservation (netsh http add urlacl) for the user who runs the app.", fatal: false);
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task ShutdownAsync()
    {
        OperationTrail.Leave("shutdown.begin");
        try
        {
            if (_services.GetService<VoiceSession>() is { } session) await session.DisposeAsync();
            if (_services.GetService<BeastBridge>() is { } bridge) await bridge.DisposeAsync();
            OperationTrail.Leave("shutdown.complete");
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Report("shutdown", ex, "App.ShutdownAsync", fatal: false);
        }
        finally { await Log.CloseAndFlushAsync(); }
    }
}
