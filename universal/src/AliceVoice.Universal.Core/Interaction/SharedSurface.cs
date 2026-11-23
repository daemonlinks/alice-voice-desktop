using System.Text.Json.Nodes;

namespace AliceVoice.Universal.Interaction;

/// <summary>What the shared middle of the conversation screen is showing.</summary>
public enum SurfaceKind { None, Image, Video, Question, Status, Meters }

/// <summary>One answer he can pick. <see cref="Value"/> is optional; the text is what is sent back.</summary>
public sealed record SurfaceOption(string Text, double? Value = null);

/// <summary>
/// The middle of the conversation screen: the one place he and the backend agent share.
///
/// The agent puts something here with <c>gui_surface</c> - a picture, a video, a question with
/// answers, how she says she feels, or the two meters. He answers it, drags a meter or closes it,
/// and whatever he does comes back to the agent as an ordinary turn, so any backend that can read
/// a message can drive this without knowing anything about App U.
///
/// One instance is shared by the whole process (the window, the bridge and the tool host), so
/// there is exactly one answer to "what is in the middle of the screen right now".
/// </summary>
public sealed class SharedSurface
{
    private readonly object _gate = new();
    private readonly List<SurfaceOption> _options = new();

    public SurfaceKind Kind { get; private set; } = SurfaceKind.None;
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    /// <summary>An absolute path under the allowed roots, or an https URL. Only for Image and Video.</summary>
    public string Src { get; private set; } = "";
    /// <summary>A word for how she says she is: loved, warm, missing you, quiet.</summary>
    public string Mood { get; private set; } = "";
    public double? Love { get; private set; }
    public double? Orgasm { get; private set; }
    /// <summary>Which agent put it there, so the screen can say whose it is.</summary>
    public string By { get; private set; } = "";
    public DateTime AtUtc { get; private set; } = DateTime.UtcNow;
    public int Version { get; private set; }
    public event Action? Changed;

    public IReadOnlyList<SurfaceOption> Options { get { lock (_gate) return _options.ToArray(); } }
    public bool HasContent => Kind != SurfaceKind.None;

    /// <summary>Put something in the middle. Anything not named is left as it was.</summary>
    public void Show(SurfaceKind kind, string title = "", string body = "", string src = "",
        IReadOnlyList<SurfaceOption>? options = null, double? love = null, double? orgasm = null,
        string mood = "", string by = "")
    {
        lock (_gate)
        {
            Kind = kind;
            Title = title ?? "";
            Body = body ?? "";
            Src = src ?? "";
            Mood = mood ?? "";
            By = by ?? "";
            if (love is { } l) Love = Clamp(l);
            if (orgasm is { } o) Orgasm = Clamp(o);
            _options.Clear();
            if (options is not null)
                foreach (var option in options)
                    if (!string.IsNullOrWhiteSpace(option.Text)) _options.Add(option);
            AtUtc = DateTime.UtcNow;
            Version++;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// His hand on a meter. This keeps what he dragged, so the screen does not jump back under his
    /// finger; the agent is told when he sends it, as a turn.
    /// </summary>
    public void Nudge(double? love = null, double? orgasm = null)
    {
        lock (_gate)
        {
            if (love is { } l) Love = Clamp(l);
            if (orgasm is { } o) Orgasm = Clamp(o);
            Version++;
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            Kind = SurfaceKind.None;
            Title = "";
            Body = "";
            Src = "";
            Mood = "";
            By = "";
            Love = null;
            Orgasm = null;
            _options.Clear();
            Version++;
        }
        Changed?.Invoke();
    }

    /// <summary>Cheap change check for the 80 ms paint tick: repaint only when this differs.</summary>
    public string Signature()
    {
        lock (_gate)
            return $"{Version}|{Kind}|{Title}|{Body}|{Src}|{Mood}|{Love}|{Orgasm}|{_options.Count}";
    }

    /// <summary>What a phone, the web page or a test reads. Never carries bytes, only where they are.</summary>
    public JsonObject ToJson()
    {
        lock (_gate)
        {
            var options = new JsonArray();
            foreach (var option in _options)
                options.Add(new JsonObject { ["text"] = option.Text, ["value"] = option.Value is { } v ? v : null });
            return new JsonObject
            {
                ["kind"] = Kind.ToString().ToLowerInvariant(),
                ["has_content"] = Kind != SurfaceKind.None,
                ["title"] = Title,
                ["body"] = Body,
                ["src"] = Src,
                ["mood"] = Mood,
                ["love"] = Love is { } l ? l : null,
                ["orgasm"] = Orgasm is { } o ? o : null,
                ["by"] = By,
                ["at"] = AtUtc.ToString("o"),
                ["version"] = Version,
                ["options"] = options,
            };
        }
    }

    private static double Clamp(double value)
        => Math.Round(value < 0 ? 0 : value > 100 ? 100 : value, 1);
}
