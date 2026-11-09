using System.Text.Json;
using System.Text.Json.Nodes;

namespace AliceVoice.Universal.Host;

public enum LaunchChoice
{
    Run,
    InstallShortcuts,
    ActivateExisting
}

/// <summary>One .lnk the installer writes. The target is the App U executable, never a script.</summary>
public sealed record ShortcutSpec(
    string LinkPath,
    string TargetPath,
    string WorkingDirectory,
    string Arguments,
    string IconLocation,
    string Description);

public static class AppLaunch
{
    public const string DisplayName = "Alice Voice";
    public const string WindowTitle = "Alice Voice App U";
    public const string ShortcutFileName = "Alice Voice.lnk";
    public const string InstallArgument = "--install-shortcuts";
    public const string SingleInstanceName = @"Local\AliceVoice.AppU";

    public static LaunchChoice Choose(IEnumerable<string> args, bool anotherInstanceRunning)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, InstallArgument, StringComparison.OrdinalIgnoreCase))
                return LaunchChoice.InstallShortcuts;
        }
        return anotherInstanceRunning ? LaunchChoice.ActivateExisting : LaunchChoice.Run;
    }

    /// <summary>
    /// Desktop and Start menu shortcuts only. This does not pin the taskbar or the Start menu.
    /// </summary>
    public static IReadOnlyList<ShortcutSpec> Shortcuts(string exePath, string desktopDirectory, string startMenuDirectory)
    {
        var exe = Path.GetFullPath(exePath);
        var workingDirectory = Path.GetDirectoryName(exe) ?? "";
        return
        [
            Spec(desktopDirectory, exe, workingDirectory),
            Spec(startMenuDirectory, exe, workingDirectory)
        ];
    }

    private static ShortcutSpec Spec(string folder, string exe, string workingDirectory) => new(
        LinkPath: Path.Combine(folder, ShortcutFileName),
        TargetPath: exe,
        WorkingDirectory: workingDirectory,
        Arguments: "",
        IconLocation: exe + ",0",
        Description: DisplayName);
}

/// <summary>
/// Bridge listen address for a shortcut install. The published build has no bind.
/// An existing appsettings value is kept. An environment value is saved only when
/// it is a Tailscale address and the file does not already name one.
/// </summary>
public static class LauncherBridgeConfig
{
    public static string? BindToSave(string? existingBind, string? environmentBind)
    {
        if (!string.IsNullOrWhiteSpace(existingBind)) return null;
        if (string.IsNullOrWhiteSpace(environmentBind)) return null;
        if (!BeastServiceUrls.TryParseBind(environmentBind.Trim(), out var host, out var port)) return null;
        if (!BridgeBindPolicy.Evaluate(host, allowUnsafe: false).Allowed) return null;
        return host + ":" + port;
    }

    public static string? ReadBind(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
            var value = Property(node, "BridgeBind");
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns updated JSON, or null when the file must be left untouched.</summary>
    public static string? JsonWithBind(string? existingJson, string bind)
    {
        JsonObject obj;
        if (string.IsNullOrWhiteSpace(existingJson))
            obj = new JsonObject();
        else
        {
            try
            {
                if (JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) is not JsonObject parsed)
                    return null;
                obj = parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (ReadBind(obj.ToJsonString()) is not null) return null;
        obj["BridgeBind"] = bind;
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? Property(JsonObject? obj, string name)
    {
        if (obj is null) return null;
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }
}
