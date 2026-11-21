using AliceVoice.Core.Config;
using AliceVoice.Core.Logging;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Composition;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AliceVoice.Universal.Service;

/// <summary>
/// The Alice Voice backend as a headless process: no window, no frontend, no MAUI.
///
/// This is the "unified backend" side of App U. The Beast is where Ollama lives, so it is where the
/// services live: this process serves the same <c>/v1</c> surface the phone already talks to -
/// speech-to-text, SOUL, Alice LOCAL, the fleet, Piper, media, and the app surface
/// (<c>/v1/app</c>, <c>/v1/app/tools</c>, <c>/v1/app/media</c>, <c>/v1/app/display</c>) - so any
/// frontend, including one not written yet, is a client of this and nothing else.
///
/// It composes the container exactly as the MAUI head does (see MauiProgram.CreateMauiApp) minus the
/// UI: the same UniversalHost, the same UniversalServices, the same BeastBridge. Running it as
/// <c>AHC_AliceVoiceBackend</c> is a Windows service via NSSM, so there is never a console window.
///
/// A service must not exit. NSSM pauses a service whose process ends, and an instant respawn loop
/// hides a real fault, so a missing or refused bridge is logged and the process stays up - visible
/// in the log rather than invisible in a restart storm.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CrashDiagnostics.Install();
        try
        {
            var preview = AliceVoiceOptions.Load();
            var layout = new HostLayout(
                AliceVoiceOptions.DefaultSettingsFile,
                preview.LogsDir,
                Path.Combine(preview.AppDataDir, "voice-assignments.json"),
                null);
            var host = UniversalHost.Prepare(PlatformHostKind.Windows, layout);
            UniversalLogging.Configure(host.LogDirectory);
            OperationTrail.Leave("service.start", host.BridgeEnabled ? "bridge" : "no-bridge");
            Log.Information("Alice Voice backend starting (headless). Bridge enabled: {Enabled} on {Host}:{Port}",
                host.BridgeEnabled, host.BridgeBindHost ?? "-", host.BridgeBindPort?.ToString() ?? "-");

            var services = new ServiceCollection();
            services.AddUniversal(host);
            services.AddSingleton<VoiceSession>();
            await using var provider = services.BuildServiceProvider();

            await using var bridge = provider.GetService<BeastBridge>();
            if (bridge is null)
            {
                Log.Error("No bridge is configured, so there is nothing to serve. Set ALICEVOICE_BRIDGE_BIND to the "
                          + "Tailscale address (for example 100.64.237.3:8765) and restart this service. Staying up "
                          + "rather than exiting, so this reads as a misconfiguration and not a crash loop.");
            }
            else
            {
                try
                {
                    await bridge.StartAsync();
                    Log.Information("Alice Voice backend listening on {Prefix}", bridge.Prefix);
                }
                catch (Exception ex)
                {
                    // A refused bind must not take the process down. Most often a missing URL
                    // reservation: netsh http add urlacl url=<prefix> user=<account>.
                    CrashDiagnostics.Report("startup", ex, "BeastBridge.StartAsync in the headless service", fatal: false);
                    Log.Error(ex, "The bridge did not start. Check the bind address and the URL reservation.");
                }
            }

            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { CrashDiagnostics.Report("unhandled", e.ExceptionObject as Exception ?? new InvalidOperationException("unknown"), "service", fatal: false); }
                catch { }
            };
            await stop.Task;

            Log.Information("Alice Voice backend stopping");
            return 0;
        }
        catch (Exception ex)
        {
            try { CrashDiagnostics.Report("startup", ex, "AliceVoice.Universal.Service.Main"); } catch { }
            Log.Fatal(ex, "Alice Voice backend failed to start");
            return 1;
        }
        finally { await Log.CloseAndFlushAsync(); }
    }
}
