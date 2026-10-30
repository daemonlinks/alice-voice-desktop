using Microsoft.Extensions.DependencyInjection;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;

namespace AliceVoice.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services, AliceVoiceOptions? options = null)
    {
        var opts = options ?? AliceVoiceOptions.Load();
        services.AddSingleton(opts);
        services.AddSingleton<IGpuSelector>(_ => new NvidiaGpuSelector(opts.SttGpuIndex));
        services.AddSingleton<ISttClient, FasterWhisperSttClient>();
        services.AddSingleton<IAppUpdater>(_ => new HttpAppUpdater(opts.ResolvedUpdateManifestUrl));
        services.AddSingleton<IVoiceBackendManager, VoiceBackendManager>();
        services.AddSingleton<IVoiceProviderManager, VoiceProviderManager>();
        services.AddSingleton<IFleetChat, HermesApiFleetChat>();
        return services;
    }
}
