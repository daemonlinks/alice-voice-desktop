using Microsoft.Extensions.DependencyInjection;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers audio services. Requires an <see cref="AliceVoiceOptions"/> singleton.</summary>
    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddSingleton<IMicrophoneCapture, WasapiMicrophoneCapture>();
        services.AddSingleton<ITtsPlayback, WasapiTtsPlayback>();
        services.AddSingleton<SapiLocalTts>();
        services.AddSingleton<ILocalTts>(sp => sp.GetRequiredService<SapiLocalTts>());

        // Voice providers (AllTalk direct HTTP + SAPI + Piper local CLI); paths/URLs from options.
        services.AddSingleton<IVoiceProvider>(sp => new AllTalkTtsProvider(sp.GetRequiredService<ITtsPlayback>(), sp.GetRequiredService<AliceVoiceOptions>()));
        services.AddSingleton<IVoiceProvider>(sp => new SapiTtsProvider(sp.GetRequiredService<ILocalTts>()));
        services.AddSingleton<IVoiceProvider>(sp => new PiperTtsProvider(sp.GetRequiredService<ITtsPlayback>(), sp.GetRequiredService<AliceVoiceOptions>()));
        return services;
    }
}
