using System.Text.Json;
using System.Text.Json.Nodes;

namespace AliceVoice.Universal.Session;

/// <summary>
/// The two switches that must survive a restart, kept in the same settings file as the bridge
/// address (so a new key never needs a new file). Both default ON: she speaks, and the
/// conversation follows itself.
/// </summary>
public sealed class UiPreferences
{
    private readonly string? _settingsFile;

    public UiPreferences(string? settingsFile) => _settingsFile = settingsFile;

    public bool SpeakReplies { get; private set; } = true;
    public bool FollowConversation { get; private set; } = true;

    public static UiPreferences Load(string? settingsFile)
    {
        var prefs = new UiPreferences(settingsFile);
        if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile)) return prefs;
        try
        {
            var text = File.ReadAllText(settingsFile);
            if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) is JsonObject root)
            {
                prefs.SpeakReplies = Read(root, "SpeakReplies", true);
                prefs.FollowConversation = Read(root, "FollowConversation", true);
            }
        }
        catch (JsonException) { /* a broken settings file must not stop the app; defaults stand */ }
        catch (IOException) { }
        return prefs;
    }

    public void Save(bool speakReplies, bool followConversation)
    {
        SpeakReplies = speakReplies;
        FollowConversation = followConversation;
        if (string.IsNullOrWhiteSpace(_settingsFile)) return;
        try
        {
            JsonObject root;
            if (File.Exists(_settingsFile))
            {
                var text = File.ReadAllText(_settingsFile);
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }
            Set(root, "SpeakReplies", speakReplies);
            Set(root, "FollowConversation", followConversation);
            var directory = Path.GetDirectoryName(_settingsFile);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            /* a switch that cannot be written is still honoured for this session */
        }
    }

    private static bool Read(JsonObject root, string name, bool fallback)
    {
        foreach (var pair in root)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Value is JsonValue value && value.TryGetValue(out bool parsed)) return parsed;
            if (bool.TryParse(pair.Value?.ToString(), out var text)) return text;
        }
        return fallback;
    }

    private static void Set(JsonObject root, string name, bool value)
    {
        string? existing = null;
        foreach (var pair in root)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            existing = pair.Key;
            break;
        }
        if (existing is not null && existing != name) root.Remove(existing);
        root[existing ?? name] = value;
    }
}
