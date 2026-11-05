using AliceVoice.Core.Config;
using AliceVoice.Universal.Composition;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Session;
using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.AppU;

public static partial class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var kind = HostKind();
        var layout = LayoutFor(kind);
        var host = UniversalHost.Prepare(kind, layout);
        UniversalLogging.Configure(host.LogDirectory);

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddUniversal(host);
        RegisterPlatform(builder.Services, host);
        builder.Services.AddSingleton<VoiceSession>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }

    private static PlatformHostKind HostKind()
    {
#if ANDROID
        return PlatformHostKind.Android;
#else
        return PlatformHostKind.Windows;
#endif
    }

    private static HostLayout LayoutFor(PlatformHostKind kind)
    {
        if (kind == PlatformHostKind.Android)
        {
            var root = SafeAppData();
            return new HostLayout(
                Path.Combine(root, "appsettings.json"),
                Path.Combine(root, "logs"),
                Path.Combine(root, "voice-assignments.json"),
                Path.Combine(root, "backgrounds"));
        }

        var preview = AliceVoiceOptions.Load();
        return new HostLayout(
            AliceVoiceOptions.DefaultSettingsFile,
            preview.LogsDir,
            Path.Combine(preview.AppDataDir, "voice-assignments.json"),
            null);
    }

    private static string SafeAppData()
    {
        try { return FileSystem.AppDataDirectory; }
        catch { return Path.Combine(Path.GetTempPath(), "AliceVoice"); }
    }

    static partial void RegisterPlatform(IServiceCollection services, UniversalHost host);
}
