using AliceVoice.Core.Logging;
using Serilog;
using Serilog.Events;

namespace AliceVoice.Universal.Host;

/// <summary>
/// Same rolling file + console log as the WinUI app. The Windows Event Log
/// sink is only attached on Windows (via <see cref="LoggingBootstrap"/>); Android
/// has no Event Log and must not try to open one.
/// </summary>
public static class UniversalLogging
{
    public static void Configure(string logDir)
    {
        Directory.CreateDirectory(logDir);
        if (OperatingSystem.IsWindows())
        {
            LoggingBootstrap.Configure(logDir);
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "AliceVoiceAppU")
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "alice-voice-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                buffered: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
