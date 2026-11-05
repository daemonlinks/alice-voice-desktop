using AliceVoice.Core;
using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Gpu;
using AliceVoice.Core.Stt;
using AliceVoice.Core.Updates;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Bridge;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.Universal.Composition;

public static class UniversalServices
{
    /// <summary>
    /// Registers the shared services. The head still registers the microphone,
    /// speech players, voice providers and <see cref="Wake.IWakeSource"/>, then
    /// <see cref="Session.VoiceSession"/>.
    /// </summary>
    public static IServiceCollection AddUniversal(this IServiceCollection services, UniversalHost host)
    {
        var options = host.Options;
        services.AddSingleton(host);
        services.AddSingleton(options);
        services.AddSingleton<SoulReloadHub>();
        services.AddSingleton(new VoiceAssignmentStore(host.AssignmentPath));
        services.AddSingleton<IAppUpdater>(_ => new HttpAppUpdater(
            options.ResolvedUpdateManifestUrl,
            packagePreference: host.Kind == PlatformHostKind.Android ? UpdatePackagePreference.Android : UpdatePackagePreference.Windows));

        if (host.Kind == PlatformHostKind.Windows)
            AddWindowsBackends(services, host, options);
        else
            AddAndroidBackends(services, host, options);

        services.AddSingleton<IVoiceBackend>(_ => new LocalOllamaBackend(options));
        services.AddSingleton<IVoiceBackend>(_ => new AliceVoiceAgentBackend(options));
        services.AddSingleton<IVoiceBackendManager>(sp => new VoiceBackendManager(
            sp.GetServices<IVoiceBackend>(), sp.GetRequiredService<ISttClient>(), options));
        services.AddSingleton<IVoiceProviderManager>(sp => new VoiceProviderManager(sp.GetServices<IVoiceProvider>()));
        return services;
    }

    private static void AddWindowsBackends(IServiceCollection services, UniversalHost host, AliceVoiceOptions options)
    {
        services.AddSingleton<IGpuSelector>(_ => new NvidiaGpuSelector(options.SttGpuIndex));
        services.AddSingleton<ISttClient>(sp => new FasterWhisperSttClient(options, sp.GetRequiredService<IGpuSelector>()));
        services.AddSingleton(new SoulStore(options));
        services.AddSingleton<ISoulGateway>(sp => new LocalSoulGateway(sp.GetRequiredService<SoulStore>()));
        services.AddSingleton<SharedTranscript>();
        services.AddSingleton<IAliceConversation>(sp => sp.GetRequiredService<SharedTranscript>());
        services.AddSingleton<IVoiceBackend>(sp =>
        {
            var inner = new AliceLocalBackend(options);
            var recording = new RecordingAliceLocalBackend(inner, sp.GetRequiredService<SharedTranscript>());
            sp.GetRequiredService<SoulReloadHub>().Register(recording.ReloadSoulAsync);
            return recording;
        });
        services.AddSingleton<IFleetChat>(_ => new HermesApiFleetChat(options.ResolvedHermesApiUrl));
        services.AddSingleton<IPiperSynth>(_ => new PiperSynth(options));
        if (host.BridgeEnabled)
        {
            services.AddSingleton(sp => new BeastBridge(
                options,
                sp.GetRequiredService<ISttClient>(),
                sp.GetRequiredService<ISoulGateway>(),
                sp.GetRequiredService<IPiperSynth>(),
                host.BridgeBindHost!,
                host.BridgeBindPort!.Value,
                sp.GetRequiredService<SoulReloadHub>(),
                () => sp.GetServices<IVoiceBackend>().First(b => b is RecordingAliceLocalBackend),
                transcript: sp.GetRequiredService<SharedTranscript>(),
                allowUnsafeBind: host.AllowUnsafeBridgeBind));
        }
    }

    private static void AddAndroidBackends(IServiceCollection services, UniversalHost host, AliceVoiceOptions options)
    {
        services.AddSingleton<IGpuSelector, NoLocalGpuSelector>();
        if (host.Remote is { } remote)
        {
            services.AddSingleton(new BeastBridgeClient(remote));
            services.AddSingleton<IAliceConversation>(sp => new RemoteAliceConversation(sp.GetRequiredService<BeastBridgeClient>()));
            services.AddSingleton<ISttClient>(sp => new RemoteSttClient(sp.GetRequiredService<BeastBridgeClient>()));
            services.AddSingleton<ISoulGateway>(sp => new RemoteSoulGateway(sp.GetRequiredService<BeastBridgeClient>()));
            services.AddSingleton<IVoiceBackend>(sp => new BridgeAliceLocalBackend(sp.GetRequiredService<BeastBridgeClient>()));
            services.AddSingleton<IFleetChat>(sp => new BridgeFleetChat(sp.GetRequiredService<BeastBridgeClient>()));
        }
        else
        {
            services.AddSingleton<IAliceConversation>(_ => new SharedTranscript());
            services.AddSingleton<ISttClient, UnconfiguredRemoteSttClient>();
            services.AddSingleton<ISoulGateway, RefusingSoulGateway>();
            services.AddSingleton<IVoiceBackend, UnconfiguredAliceLocalBackend>();
            services.AddSingleton<IFleetChat>(_ => new HermesApiFleetChat(options.ResolvedHermesApiUrl));
        }
    }
}
