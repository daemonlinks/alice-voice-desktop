using AliceVoice.Core.Config;
using AliceVoice.Core.WakeWord;

namespace AliceVoice.Universal.Wake;

/// <summary>
/// The numbers the WinUI window actually uses (not the options helpers that
/// default patience to 1). Patience and threshold come from the verified model
/// unless an env/appsettings override is set. Sensitivity on the slider is
/// <c>1 - threshold</c>.
/// </summary>
public static class WakeWordSetup
{
    public static WakeWordSessionSettings Create(AliceVoiceOptions options, WakeWordModelInfo model)
    {
        var threshold = Math.Clamp(options.WakeWordThreshold ?? model.Entry.Threshold, 0.1f, 0.9f);
        return new WakeWordSessionSettings
        {
            Threshold = threshold,
            Patience = Math.Max(1, options.WakeWordPatience ?? model.Entry.Patience),
            EndOfSpeech = new EndOfSpeechSettings
            {
                SpeechRms = options.ResolvedWakeSpeechRms,
                EndSilenceMs = options.ResolvedWakeEndSilenceMs,
                NoSpeechTimeoutMs = options.ResolvedWakeNoSpeechTimeoutMs,
                MaxUtteranceMs = options.ResolvedWakeMaxUtteranceSeconds * 1000,
            }
        };
    }

    public static double SensitivityFor(float threshold) => Math.Clamp(Math.Round(1 - threshold, 2), 0.1, 0.9);

    public static float ThresholdFor(double sensitivity) => (float)Math.Round(1 - sensitivity, 2);
}
