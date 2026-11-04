using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Wake;
using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.AppU;

public static partial class MauiProgram
{
    static partial void RegisterPlatform(IServiceCollection services, UniversalHost host)
    {
        services.AddSingleton<IPcmMicrophone, AndroidMicrophone>();
        services.AddSingleton<IPcmPlayer, AndroidPcmPlayer>();
        services.AddSingleton<IVoiceProvider>(sp => new AllTalkVoiceProvider(sp.GetRequiredService<IPcmPlayer>(), sp.GetRequiredService<AliceVoiceOptions>()));
        services.AddSingleton<IVoiceProvider, AndroidSpeechProvider>();
        services.AddSingleton<IVoiceProvider>(sp => new RemotePiperProvider(sp.GetRequiredService<IPcmPlayer>(), sp.GetService<BeastBridgeClient>()));
        services.AddSingleton<IWakeSource>(sp => new AndroidWakeSource(sp.GetRequiredService<AliceVoiceOptions>()));
    }
}
