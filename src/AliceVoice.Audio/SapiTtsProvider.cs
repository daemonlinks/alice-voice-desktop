using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Windows SAPI TTS as a voice provider. Instant, local, no network. The
/// "voice" is a SAPI voice name (e.g. "Microsoft Hazel Desktop"); empty uses
/// the system default.
/// </summary>
public sealed class SapiTtsProvider : IVoiceProvider
{
    private readonly SapiLocalTts _sapi;

    public SapiTtsProvider(SapiLocalTts sapi) => _sapi = sapi;

    public VoiceProviderKind Kind => VoiceProviderKind.Sapi;
    public string DisplayName => "SAPI";

    public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        var voices = _sapi.AvailableVoices();
        return Task.FromResult<IReadOnlyList<string>>(voices);
    }

    public Task SpeakAsync(string text, string voice, CancellationToken ct = default)
    {
        _sapi.Speak(text, voice);
        return Task.CompletedTask;
    }
}
