using Serilog;
using Serilog.Events;

namespace AliceVoice.Core.Logging;

/// <summary>
/// Best-practice structured logging: rolling file + Windows Event Log + console.
/// Replaces the ad-hoc logging of the legacy web app with a proper pipeline.
/// </summary>
public static class LoggingBootstrap
{
    public static void Configure()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AliceVoice", "logs");

        Directory.CreateDirectory(logDir);

        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "AliceVoice")
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "alice-voice-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.EventLog(
                source: "AliceVoice",
                manageEventSource: true,
                restrictedToMinimumLevel: LogEventLevel.Warning);

        Log.Logger = config.CreateLogger();
    }
}
