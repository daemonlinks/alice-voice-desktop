using AliceVoice.Audio;
using AliceVoice.Core.Config;
using AliceVoice.Inference.WakeWord;
using AliceVoice.Universal.Audio;
using AliceVoice.Universal.Host;
using AliceVoice.Universal.Wake;
using Microsoft.Extensions.DependencyInjection;

namespace AliceVoice.AppU;

public static partial class MauiProgram
{
    static partial void RegisterPlatform(IServiceCollection services, UniversalHost host)
    {
        services.AddAudioServices();
        services.AddSingleton<IPcmMicrophone>(sp => new WasapiMicAdapter(sp.GetRequiredService<IMicrophoneCapture>()));
        services.AddSingleton<IWakeSource>(sp => new WindowsWakeSource(sp.GetRequiredService<AliceVoiceOptions>()));
    }
}

/// <summary>NAudio capture from the original audio project, as the session's microphone.</summary>
public sealed class WasapiMicAdapter : IPcmMicrophone
{
    private readonly IMicrophoneCapture _inner;

    public WasapiMicAdapter(IMicrophoneCapture inner)
    {
        _inner = inner;
        _inner.AudioFrameCaptured += frame => AudioFrameCaptured?.Invoke(frame);
    }

    public event Action<float[]>? AudioFrameCaptured;
    public Task StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);
    public Task StopAsync() => _inner.StopAsync();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public sealed class WindowsWakeSource : IWakeSource
{
    private readonly AliceVoiceOptions _options;
    public WindowsWakeSource(AliceVoiceOptions options) => _options = options;

    public Task<IWakeController> OpenAsync(CancellationToken ct = default)
        => Task.FromResult(WakeControllerFactory.Create(_options, OnnxWakeWordFactory.CreateDetector));
}
