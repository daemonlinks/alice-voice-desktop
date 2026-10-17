using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.Audio;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddSingleton<IMicrophoneCapture, WasapiMicrophoneCapture>();
        return services;
    }
}
