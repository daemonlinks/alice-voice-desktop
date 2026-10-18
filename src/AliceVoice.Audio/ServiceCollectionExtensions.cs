using Microsoft.Extensions.DependencyInjection;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddSingleton<IMicrophoneCapture, WasapiMicrophoneCapture>();
        services.AddSingleton<ITtsPlayback, WasapiTtsPlayback>();
        services.AddSingleton<SapiLocalTts>();
        services.AddSingleton<ILocalTts>(sp => sp.GetRequiredService<SapiLocalTts>());

        // Voice providers (AllTalk direct HTTP + SAPI).
        services.AddSingleton<IVoiceProvider, AllTalkTtsProvider>();
        services.AddSingleton<IVoiceProvider, SapiTtsProvider>();
        return services;
    }
}
