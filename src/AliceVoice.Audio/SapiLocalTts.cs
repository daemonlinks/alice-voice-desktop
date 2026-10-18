using System.Speech.Synthesis;
using Serilog;
using AliceVoice.Core.Voice;

namespace AliceVoice.Audio;

/// <summary>
/// Instant local TTS via Windows SAPI (System.Speech). No network, no relay —
/// magnitude faster than streaming TTS from the relay. Used by the local
/// Ollama backend state and as a voice provider.
/// </summary>
public sealed class SapiLocalTts : ILocalTts
{
    private readonly SpeechSynthesizer _synth = new();

    public SapiLocalTts()
    {
        _synth.Rate = 0;
        Log.Information("SAPI local TTS ready (voice: {Voice})", _synth.Voice?.Name ?? "default");
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Log.Information("SAPI speaking: {Text}", text);
        _synth.Speak(text);
    }

    public void Speak(string text, string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!string.IsNullOrEmpty(voiceName))
        {
            try
            {
                _synth.SelectVoice(voiceName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SAPI voice {Voice} unavailable, using default", voiceName);
            }
        }
        Log.Information("SAPI speaking ({Voice}): {Text}", voiceName ?? "default", text);
        _synth.Speak(text);
    }

    public IReadOnlyList<string> AvailableVoices()
    {
        try
        {
            return _synth.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SAPI voice enumeration failed");
            return new List<string>();
        }
    }

    public void Stop() => _synth.SpeakAsyncCancelAll();
}
