using Microsoft.Extensions.DependencyInjection;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddSingleton<IMicrophoneCapture, WasapiMicrophoneCapture>();
        services.AddSingleton<ITtsPlayback, WasapiTtsPlayback>();
        services.AddSingleton<ILocalTts, SapiLocalTts>();
        return services;
    }
}
