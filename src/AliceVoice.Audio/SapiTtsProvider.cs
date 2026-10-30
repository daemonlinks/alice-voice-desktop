using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Windows SAPI TTS as a voice provider. Instant, local, no network. The
/// "voice" is a SAPI voice name (e.g. "Microsoft Hazel Desktop"); empty uses
/// the system default. Speech is asynchronous and cancellable.
/// </summary>
public sealed class SapiTtsProvider : IVoiceProvider
{
    private readonly ILocalTts _sapi;

    public SapiTtsProvider(ILocalTts sapi) => _sapi = sapi;

    public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
    public string DisplayName => "SAPI";

    public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
        => Task.FromResult(_sapi.AvailableVoices());

    public Task SpeakAsync(string text, string voice, CancellationToken ct = default)
        => _sapi.SpeakAsync(text, voice, ct);
}
