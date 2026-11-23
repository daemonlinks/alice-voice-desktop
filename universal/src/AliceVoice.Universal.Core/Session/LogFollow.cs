namespace AliceVoice.Universal.Session;

/// <summary>
/// The bottom of the log, and whether he is looking at it.
///
/// Master, 2026-11-23: *"I always want to see the latest log entry unless I am looking back in the
/// log."* So the view chases the newest line only while he is already at the end of it, and holds
/// still the moment he scrolls up to read. Pure arithmetic, kept here so it can be tested without a
/// window - the page just feeds it what the scroll view reports.
/// </summary>
public static class LogFollow
{
    /// <summary>How close to the end still counts as the end. A scroll view is rarely flush to the
    /// pixel, and one repaint that shifts a wrapped line would otherwise unstick him mid-read.</summary>
    public const double Slack = 24;

    /// <summary>The furthest the view can scroll: content height minus the window onto it.</summary>
    public static double MaxScroll(double contentHeight, double viewportHeight)
        => Math.Max(0, contentHeight - viewportHeight);

    /// <summary>True when he is at (or within a hair of) the newest line.</summary>
    public static bool AtBottom(double scrollY, double contentHeight, double viewportHeight, double slack = Slack)
        => scrollY >= MaxScroll(contentHeight, viewportHeight) - slack;
}
