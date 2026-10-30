using Serilog;
using Serilog.Events;

namespace AliceVoice.Core.Logging;

/// <summary>
/// Structured logging: rolling file + console, plus the Windows Event Log
/// (warnings and above) when it is available. Creating the "AliceVoice"
/// event source needs admin rights the first time; if that fails the app
/// logs to file only instead of crashing at startup.
/// </summary>
public static class LoggingBootstrap
{
    public static void Configure(string? logDir = null)
    {
        logDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AliceVoice", "logs");
        Directory.CreateDirectory(logDir);

        LoggerConfiguration Base() => new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "AliceVoice")
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "alice-voice-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        string? eventLogError = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Log.Logger = Base()
                    .WriteTo.EventLog(source: "AliceVoice", manageEventSource: true, restrictedToMinimumLevel: LogEventLevel.Warning)
                    .CreateLogger();
                return;
            }
            catch (Exception ex) { eventLogError = ex.Message; }
        }

        Log.Logger = Base().CreateLogger();
        if (eventLogError is not null) Log.Warning("Windows Event Log sink unavailable ({Error}); logging to file only", eventLogError);
    }
}
