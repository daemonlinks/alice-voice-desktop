using System.Text.Json.Nodes;

namespace AliceVoice.Core.Voice;

/// <summary>A reply after the display filter: the words to show and speak, the display commands to run, and the tool names that were ignored.</summary>
public sealed record FilteredReply(string Prose, IReadOnlyList<ToolCommand> Display, IReadOnlyList<string> Ignored);

/// <summary>
/// The tight allowlist for tool commands that arrive inside a Hermes or fleet reply.
/// Only <c>gui_show_image</c> and <c>gui_image_clear</c> are executed - by the app, on
/// its own screen. Every other command is stripped and ignored. Nothing is reported
/// back to the agent: no result, no ack, no screen state.
/// </summary>
public static class DisplayCommands
{
    public const string ShowImage = "gui_show_image";
    public const string ClearImage = "gui_image_clear";
    public const string EventType = "gui_command";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { ShowImage, ClearImage };

    public static bool IsAllowed(string tool) => Allowed.Contains(tool);

    public static FilteredReply Filter(string? reply)
    {
        var parsed = ToolCommandParser.Parse(reply);
        if (!parsed.HasCommands) return new FilteredReply(parsed.Prose, Array.Empty<ToolCommand>(), Array.Empty<string>());
        var display = new List<ToolCommand>();
        var ignored = new List<string>();
        foreach (var c in parsed.Commands)
        {
            if (IsAllowed(c.Tool)) display.Add(c);
            else ignored.Add(c.Tool);
        }
        return new FilteredReply(parsed.Prose, display, ignored);
    }

    /// <summary>The event a backend raises for one allowlisted command. The args travel as a JSON object, never as text.</summary>
    public static BackendEvent ToEvent(ToolCommand command)
    {
        JsonObject args;
        try { args = JsonNode.Parse(command.ArgsJson) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { args = new JsonObject(); }
        return new BackendEvent(EventType, command.Tool, new JsonObject { ["tool"] = command.Tool, ["args"] = args });
    }
}
