using System.Text.Json.Nodes;
using AliceVoice.Core.Voice;
using AliceVoice.Universal.Media;

namespace AliceVoice.Universal.Tools;

/// <summary>
/// App U's own tools, run on this app's screen: the display trio the local brain is allowed to call.
/// This is the app's end of "a backend can drive the GUI". Args are checked against the same share
/// rules as every other path, the command goes through the one <see cref="ImageDisplay"/> the screen
/// paints from, and the answer comes back in the web page's shape (<c>{"ok":true,...}</c> /
/// <c>{"error":"..."}</c>) with the relay's own words for a refusal - so the app and the page answer
/// identically to the same mistake, and a model can correct itself instead of being ignored.
/// </summary>
public sealed class AppToolHost
{
    public const string ListPictures = "gui_list_pictures";

    private readonly ImageDisplay _images;

    public AppToolHost(ImageDisplay images) => _images = images;

    /// <summary>The tools this host owns. Anything else falls through to <c>alice_local_tools.py</c> untouched.</summary>
    public static bool Owns(string? tool)
        => tool is ListPictures or DisplayCommands.ShowImage or DisplayCommands.ClearImage;

    /// <summary>Null means "not mine" - the caller runs its own path for that tool.</summary>
    public Task<string?> RunAsync(string tool, string argsJson, CancellationToken ct = default)
        => Task.FromResult(Owns(tool) ? Run(tool, argsJson) : null);

    private string Run(string tool, string argsJson)
    {
        if (!Owns(tool)) return Error("unknown tool: " + tool);
        var args = Parse(argsJson);
        return tool switch
        {
            ListPictures => List(args),
            DisplayCommands.ShowImage => Show(args),
            DisplayCommands.ClearImage => Clear(args),
            _ => Error("unknown tool: " + tool),
        };
    }

    /// <summary>What may I show him? Names only, never a path - and the web page's answer field for field.</summary>
    private string List(JsonObject args)
    {
        if (_images.Share is not { } share) return Error("no pictures on this device (they live on the Beast)");
        var n = 12;
        if (args["n"] is { } node && int.TryParse(node.ToString(), out var asked)) n = asked;
        var names = share.Catalogue(n);
        var array = new JsonArray();
        foreach (var name in names) array.Add(name);
        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = names.Count,
            ["names"] = array,
            ["how"] = "gui_show_image {\"file\":\"<one of these names>\"}",
        }.ToJsonString();
    }

    /// <summary>
    /// Show a picture: the same command the reply path runs, through the same <see cref="ImageDisplay"/>,
    /// so there is exactly one set of rules. A refusal is reported exactly as the rules word it, with
    /// the page's hint, so the model can try a name that exists. An accepted picture is queued for
    /// painting; the app log carries whether it actually painted.
    /// </summary>
    private string Show(JsonObject args)
    {
        var src = (Text(args, "src") ?? Text(args, "file") ?? Text(args, "name"))?.Trim();
        if (string.IsNullOrEmpty(src)) return Error("bad args: src", hint: true);
        var mode = (Text(args, "mode") ?? "stage").Trim().ToLowerInvariant();
        if (mode is not ("stage" or "card" or "backdrop")) return Error("bad args: mode");

        if (_images.Share is { } share)
        {
            var isUrl = src.Contains("://", StringComparison.Ordinal);
            var check = !isUrl && Path.IsPathFullyQualified(src) ? share.Validate(src) : share.ResolveName(src);
            if (!check.Ok)
            {
                // An https URL is the one thing this share cannot judge (the loader fetches it).
                if (!(isUrl && _images.Settings.AllowHttps))
                    return Error(check.Error ?? "not a file", hint: !Path.IsPathFullyQualified(src));
            }
            else if (!_images.Settings.AllowedKinds.Contains(check.Kind))
            {
                return Error("unsupported type");
            }
        }

        _images.Submit(new ToolCommand(DisplayCommands.ShowImage, args.ToJsonString()));
        return new JsonObject
        {
            ["ok"] = true,
            ["accepted"] = true,
            ["mode"] = mode,
            ["note"] = "the app is painting it now; the app log says whether it painted",
        }.ToJsonString();
    }

    private string Clear(JsonObject args)
    {
        var wasShowing = _images.State.Current is not null || _images.State.Backdrop is not null;
        _images.Submit(new ToolCommand(DisplayCommands.ClearImage, args.ToJsonString()));
        return new JsonObject { ["ok"] = true, ["cleared"] = wasShowing }.ToJsonString();
    }

    private string Error(string error, bool hint = false)
    {
        var obj = new JsonObject { ["error"] = error };
        if (hint && _images.Share is { } share) obj["hint"] = share.CatalogueHint();
        return obj.ToJsonString();
    }

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static string? Text(JsonObject args, string key)
        => args.TryGetPropertyValue(key, out var node) ? node?.ToString() : null;
}
