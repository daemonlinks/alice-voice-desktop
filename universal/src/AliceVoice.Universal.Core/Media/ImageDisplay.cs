using System.Net;
using System.Text.Json;
using AliceVoice.Core.Voice;

namespace AliceVoice.Universal.Media;

/// <summary>
/// Runs the two allowlisted display commands (<c>gui_show_image</c>, <c>gui_image_clear</c>)
/// on this device's own screen. Loading happens off the turn: <see cref="Submit"/> returns
/// at once, so speech and the next turn never wait for a picture. Every outcome is written
/// to the app log only. Nothing is reported back to the agent.
/// </summary>
public sealed class ImageDisplay : IDisposable
{
    private readonly MediaSettings _settings;
    private readonly MediaShare? _localShare;
    private readonly Func<string, string>? _bridgeUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private int _seq;
    private Task _pending = Task.CompletedTask;
    private bool _offNoted;

    /// <param name="localShare">Set where this device may read the allowed roots itself (Windows). Null on phones.</param>
    /// <param name="bridgeUrl">Bridge media URL for an id (<see cref="MediaShare.ToId"/>). Null when there is no bridge to dial.</param>
    public ImageDisplay(MediaSettings settings, MediaShare? localShare, Func<string, string>? bridgeUrl, Action<string> log,
        Func<DateTime>? clock = null, HttpClient? http = null)
    {
        _settings = settings;
        _localShare = localShare;
        _bridgeUrl = bridgeUrl;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public OverlayState State { get; } = new();
    public MediaSettings Settings => _settings;
    public event Action? Changed;

    /// <summary>Completes when the most recent load has finished (tests, and a clean shutdown).</summary>
    public Task WhenIdle() { lock (_gate) return _pending; }

    /// <summary>Run one allowlisted command. Never throws, never blocks on I/O.</summary>
    public void Submit(ToolCommand command)
    {
        if (!_settings.Enabled)
        {
            if (!_offNoted) { _offNoted = true; _log("Picture ignored: media display is off in Settings"); }
            return;
        }
        switch (command.Tool)
        {
            case DisplayCommands.ShowImage: Show(command.ArgsJson); break;
            case DisplayCommands.ClearImage: Clear(ReadString(command.ArgsJson, "id")); break;
        }
    }

    /// <summary>The close chip. The user's own action, logged like any other.</summary>
    public void CloseByUser()
    {
        if (State.Clear(null) is { } gone) { _log($"Picture closed by you ({gone.Mode.ToString().ToLowerInvariant()})"); Changed?.Invoke(); }
    }

    /// <summary>Stepping the scene puts the photo catalogue back in charge of the backdrop.</summary>
    public void ReleaseBackdrop()
    {
        if (State.ClearBackdrop() is not null) { _log("Backdrop picture released to the scene"); Changed?.Invoke(); }
    }

    public void Tick()
    {
        var gone = State.Tick(_clock());
        if (gone.Count == 0) return;
        foreach (var g in gone) _log($"Picture timed out after {g.DurationS:0.#}s ({g.Mode.ToString().ToLowerInvariant()})");
        Changed?.Invoke();
    }

    /// <summary>The page painted it (or could not). Local log only - the ack never leaves the device.</summary>
    public void NoteRendered(string id, bool ok)
    {
        var item = State.Current?.Id == id ? State.Current : State.Backdrop?.Id == id ? State.Backdrop : null;
        if (item is null) return;
        _log(ok ? $"Picture shown · {item.Mode.ToString().ToLowerInvariant()} · {item.Natural}" : $"Picture did not paint · {item.Mode.ToString().ToLowerInvariant()}");
    }

    private void Show(string argsJson)
    {
        var args = Parse(argsJson);
        // "src" is the relay's name for it; "file" is how the local leg names a picture, and the
        // web page accepts both. Either way it is one string that goes through the same rules.
        var src = (ReadString(args, "src") ?? ReadString(args, "file"))?.Trim();
        if (string.IsNullOrEmpty(src)) { _log("Picture ignored: bad args: src"); return; }
        var modeText = (ReadString(args, "mode") ?? "stage").Trim().ToLowerInvariant();
        OverlayMode mode;
        switch (modeText)
        {
            case "stage": mode = OverlayMode.Stage; break;
            case "card": mode = OverlayMode.Card; break;
            case "backdrop": mode = OverlayMode.Backdrop; break;
            default: _log("Picture ignored: bad args: mode"); return;
        }
        var caption = ReadString(args, "caption") ?? "";
        if (caption.Length > 300) caption = caption[..300];
        var keep = ReadBool(args, "keep") ?? true;
        var duration = ReadDouble(args, "duration_s") is { } d && d > 0 && d <= 3600 ? d : _settings.DefaultDurationS;
        var id = ReadString(args, "id") is { Length: > 0 and <= 64 } given ? given : Guid.NewGuid().ToString("N")[..8];

        int mySeq;
        lock (_gate) mySeq = ++_seq;
        var task = Task.Run(async () =>
        {
            try
            {
                var (bytes, kind, label, error) = await LoadAsync(src);
                if (error is not null) { _log($"Picture not shown: {error}"); return; }
                if (!ImageHeader.TryRead(bytes, out var w, out var h)) { _log("Picture not shown: load failed (not a picture App U can read)"); return; }
                lock (_gate) { if (mySeq != _seq) { _log("Picture skipped: a newer one replaced it while loading"); return; } }
                State.Show(new OverlayItem(id, mode, caption, bytes!, kind, label, keep, duration, _clock(), w, h));
                _log($"Picture loaded · {modeText} · {w}x{h} · {label}" + (keep ? "" : $" · {duration:0.#}s"));
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _log($"Picture not shown: load failed ({ex.GetType().Name})");
            }
        });
        lock (_gate) _pending = task;
    }

    private void Clear(string? target)
    {
        lock (_gate) _seq++; // a picture still loading is not shown after a clear
        var gone = State.Clear(target);
        if (gone is null)
        {
            _log(string.IsNullOrEmpty(target) ? "Clear: no picture was showing" : "Clear: that picture was not showing");
            return;
        }
        _log($"Picture cleared ({gone.Mode.ToString().ToLowerInvariant()})");
        Changed?.Invoke();
    }

    private async Task<(byte[]? Bytes, MediaKind Kind, string Label, string? Error)> LoadAsync(string src)
    {
        if (src.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri)) return (null, MediaKind.Image, "", "bad args: src");
            if (!_settings.AllowHttps) return (null, MediaKind.Image, "", "web pictures are off (allowed roots only)");
            if (uri.Scheme != Uri.UriSchemeHttps) return (null, MediaKind.Image, "", "only https:// pictures are allowed");
            var kindOk = MediaShare.TryKindOf(src, out var kind, out _);
            if (kindOk && !_settings.AllowedKinds.Contains(kind)) return (null, kind, "", Unsupported(kind));
            var fetched = await FetchAsync(uri.ToString());
            return fetched.Error is null ? (fetched.Bytes, MediaKind.Image, "web · " + uri.Host, null) : (null, MediaKind.Image, "", fetched.Error);
        }

        if (_localShare is not null)
        {
            // A bare name is how local-me asks for a picture: she can name one, she cannot be
            // expected to hand over an absolute path. The roots, type allow-list and size cap
            // still apply, and an ambiguous name is refused rather than guessed at.
            var check = Path.IsPathFullyQualified(src) ? _localShare.Validate(src) : _localShare.ResolveName(src);
            if (check.Ok)
            {
                if (!_settings.AllowedKinds.Contains(check.Kind)) return (null, check.Kind, "", Unsupported(check.Kind));
                var bytes = await File.ReadAllBytesAsync(check.FullPath!);
                return (bytes, check.Kind, "local · " + Path.GetFileName(check.FullPath), null);
            }
            // Only "the file is not on this machine" may be asked of the Beast. A refusal
            // under the rules is final everywhere, so it is never re-tried over the bridge.
            if (check.Error != MediaShare.NotAFile || _bridgeUrl is null) return (null, MediaKind.Image, "", check.Error);
        }
        if (_bridgeUrl is null) return (null, MediaKind.Image, "", "no Beast bridge to fetch it from");
        if (MediaShare.TryKindOf(src, out var k, out _) && !_settings.AllowedKinds.Contains(k)) return (null, k, "", Unsupported(k));
        var viaBridge = await FetchAsync(_bridgeUrl(MediaShare.ToId(src)));
        return viaBridge.Error is null ? (viaBridge.Bytes, MediaKind.Image, "bridge · " + SafeName(src), null) : (null, MediaKind.Image, "", viaBridge.Error);
    }

    private async Task<(byte[]? Bytes, string? Error)> FetchAsync(string url)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            return (null, resp.StatusCode switch
            {
                HttpStatusCode.Forbidden => MediaShare.PathNotAllowed,
                HttpStatusCode.NotFound => MediaShare.NotAFile,
                HttpStatusCode.UnsupportedMediaType => MediaShare.UnsupportedType,
                HttpStatusCode.RequestEntityTooLarge => MediaShare.TooLarge,
                _ => $"load failed (HTTP {(int)resp.StatusCode})"
            });
        }
        if (resp.Content.Headers.ContentLength is { } len && len > _settings.MaxBytes) return (null, MediaShare.TooLarge);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !_settings.AllowedKinds.Contains(MediaKind.Video)) return (null, Unsupported(MediaKind.Video));
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + n > _settings.MaxBytes) return (null, MediaShare.TooLarge);
            ms.Write(buffer, 0, n);
        }
        return (ms.ToArray(), null);
    }

    private static string Unsupported(MediaKind kind) => $"unsupported in App U: {kind.ToString().ToLowerInvariant()} (images only for now)";

    private static string SafeName(string path)
    {
        var name = path.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static JsonElement Parse(string json)
    {
        try { using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); return doc.RootElement.Clone(); }
        catch (JsonException) { using var empty = JsonDocument.Parse("{}"); return empty.RootElement.Clone(); }
    }

    private static string? ReadString(string json, string name) => ReadString(Parse(json), name);

    private static string? ReadString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ReadBool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static double? ReadDouble(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
