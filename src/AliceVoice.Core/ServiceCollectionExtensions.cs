using Microsoft.Extensions.DependencyInjection;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;

namespace AliceVoice.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IGpuSelector, NvidiaGpuSelector>();
        services.AddSingleton<IAppUpdater>(_ => new HttpAppUpdater(
            "https://raw.githubusercontent.com/daemonlinks/alice-voice-desktop/main/version.json"));
        services.AddSingleton<IVoiceBackendManager, VoiceBackendManager>();
        return services;
    }
}
