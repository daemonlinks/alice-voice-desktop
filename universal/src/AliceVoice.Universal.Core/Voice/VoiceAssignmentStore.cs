using System.Text.Json;
using AliceVoice.Core.Voice;

namespace AliceVoice.Universal.Voice;

/// <summary>
/// Per-target voice assignment file. Same JSON shape as the WinUI app
/// (<c>voice-assignments.json</c>, provider enum stored as a number) so a
/// Windows App U build reads the file the original app already wrote.
/// </summary>
public sealed class VoiceAssignmentStore
{
    private readonly string _path;
    public VoiceAssignmentStore(string path) => _path = path;
    public string Path => _path;

    public Dictionary<string, VoiceProfile> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        var raw = JsonSerializer.Deserialize<Dictionary<string, VoiceProfileDto>>(File.ReadAllText(_path))
                  ?? new Dictionary<string, VoiceProfileDto>();
        var map = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dto) in raw)
            map[name] = new VoiceProfile(dto.Provider, dto.Voice ?? "");
        return map;
    }

    public void Save(IReadOnlyDictionary<string, VoiceProfile> map)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var raw = map.ToDictionary(
            kv => kv.Key,
            kv => new VoiceProfileDto { Provider = kv.Value.Provider, Voice = kv.Value.Voice },
            StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(_path, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class VoiceProfileDto
    {
        public VoiceProviderKind Provider { get; set; }
        public string Voice { get; set; } = "";
    }
}
