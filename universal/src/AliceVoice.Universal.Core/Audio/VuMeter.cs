namespace AliceVoice.Universal.Audio;

/// <summary>The WinUI meter: about -40 dBFS..0 dBFS mapped onto a 100px bar.</summary>
public static class VuMeter
{
    public static double WidthPx(float level, double full = 100)
    {
        var db = 20 * Math.Log10(Math.Max(level, 1e-5));
        return Math.Clamp((db + 40) / 40 * full, 0, full);
    }
}
