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
        var host = services.GetRequiredService<UniversalHost>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(host, "domain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
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
        return window;
    }

    private async Task StartBridgeAsync()
    {
#if WINDOWS
        if (_services.GetService<BeastBridge>() is not { } bridge) return;
        try { await bridge.StartAsync(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Beast bridge did not start. A non-loopback bind on Windows needs: netsh http add urlacl url=<prefix> user=%USERNAME%");
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_services.GetService<VoiceSession>() is { } session) await session.DisposeAsync();
            if (_services.GetService<BeastBridge>() is { } bridge) await bridge.DisposeAsync();
        }
        catch (Exception ex) { Log.Warning(ex, "Shutdown failed"); }
        finally { await Log.CloseAndFlushAsync(); }
    }

    private static void WriteCrash(UniversalHost host, string kind, Exception? ex)
    {
        try
        {
            Log.Fatal(ex, "Unhandled {Kind} exception", kind);
            var dir = Path.Combine(host.LogDirectory, "..", "crash");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.txt"), $"{DateTime.Now:O}\n{kind}\n{ex}");
            Log.CloseAndFlush();
        }
        catch { /* never throw from a crash handler */ }
    }
}
