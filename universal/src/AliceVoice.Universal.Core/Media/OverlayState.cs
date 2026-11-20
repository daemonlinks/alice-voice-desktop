namespace AliceVoice.Universal.Media;

/// <summary>Where a picture goes. Same three modes as the web page (contract §2).</summary>
public enum OverlayMode
{
    /// <summary>Large centred overlay above the scene (default).</summary>
    Stage,
    /// <summary>Compact card beside the conversation.</summary>
    Card,
    /// <summary>Replaces the scene photo until cleared or the scene is stepped.</summary>
    Backdrop
}

/// <summary>One picture ready to paint. <see cref="Bytes"/> are already checked (type, size, header).</summary>
public sealed record OverlayItem(
    string Id,
    OverlayMode Mode,
    string Caption,
    byte[] Bytes,
    MediaKind Kind,
    string SourceLabel,
    bool Keep,
    double DurationS,
    DateTime ShownUtc,
    int Width,
    int Height)
{
    public DateTime? ExpiresUtc => Keep ? null : ShownUtc.AddSeconds(DurationS);
    public string Natural => $"{Width}x{Height}";
}

/// <summary>
/// The overlay's state machine, free of UI so it is testable: one stage-or-card
/// picture and one backdrop. A new picture replaces the one in its slot. A clear with
/// an empty target takes the stage/card picture down; a clear naming an id takes down
/// only that picture (the contract's <c>target</c> rule). <c>keep:false</c> pictures
/// expire on <see cref="Tick"/>.
/// </summary>
public sealed class OverlayState
{
    private readonly object _gate = new();

    public OverlayItem? Current { get; private set; }
    public OverlayItem? Backdrop { get; private set; }
    public int Version { get; private set; }

    public void Show(OverlayItem item)
    {
        lock (_gate)
        {
            if (item.Mode == OverlayMode.Backdrop) Backdrop = item;
            else Current = item;
            Version++;
        }
    }

    /// <summary>Returns what was taken down, or null when nothing matched.</summary>
    public OverlayItem? Clear(string? target)
    {
        lock (_gate)
        {
            OverlayItem? removed = null;
            if (string.IsNullOrEmpty(target))
            {
                removed = Current;
                Current = null;
            }
            else if (Current?.Id == target) { removed = Current; Current = null; }
            else if (Backdrop?.Id == target) { removed = Backdrop; Backdrop = null; }
            if (removed is not null) Version++;
            return removed;
        }
    }

    public OverlayItem? ClearBackdrop()
    {
        lock (_gate)
        {
            var removed = Backdrop;
            Backdrop = null;
            if (removed is not null) Version++;
            return removed;
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            if (Current is null && Backdrop is null) return;
            Current = null;
            Backdrop = null;
            Version++;
        }
    }

    /// <summary>Expire timed pictures. Returns the ones that went.</summary>
    public IReadOnlyList<OverlayItem> Tick(DateTime utcNow)
    {
        lock (_gate)
        {
            var gone = new List<OverlayItem>();
            if (Current?.ExpiresUtc is { } c && utcNow >= c) { gone.Add(Current); Current = null; }
            if (Backdrop?.ExpiresUtc is { } b && utcNow >= b) { gone.Add(Backdrop); Backdrop = null; }
            if (gone.Count > 0) Version++;
            return gone;
        }
    }
}
