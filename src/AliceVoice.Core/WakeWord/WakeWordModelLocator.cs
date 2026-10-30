using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AliceVoice.Core.WakeWord;

/// <summary>One file in the wake-word manifest, with its expected SHA-256.</summary>
public sealed record WakeWordFileEntry
{
    public string File { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

/// <summary>One wake-word classifier listed in <c>wakeword-models.json</c>.</summary>
public sealed record WakeWordModelEntry
{
    public string Id { get; init; } = "";
    /// <summary>What to say, as shown in the UI (e.g. "hey jarvis").</summary>
    public string Phrase { get; init; } = "";
    public string File { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public float Threshold { get; init; } = 0.5f;
    /// <summary>Consecutive 80 ms frames at or above the threshold before it fires (fewer false accepts).</summary>
    public int Patience { get; init; } = 1;
    /// <summary>True for a stand-in model (e.g. hey_jarvis until hey_alice exists).</summary>
    public bool Interim { get; init; }
    public string? License { get; init; }
    public string? Source { get; init; }
}

/// <summary><c>wakeword-models.json</c>: shared feature models + available wake words.</summary>
public sealed record WakeWordManifest
{
    public string Default { get; init; } = "";
    public WakeWordFileEntry Melspectrogram { get; init; } = new();
    public WakeWordFileEntry Embedding { get; init; } = new();
    public List<WakeWordModelEntry> Models { get; init; } = new();
}

/// <summary>A verified, ready-to-load model set.</summary>
public sealed record WakeWordModelInfo(
    string Directory,
    string MelspectrogramPath,
    string EmbeddingPath,
    string ClassifierPath,
    WakeWordModelEntry Entry)
{
    public string Id => Entry.Id;
    public string Phrase => Entry.Phrase;
    public bool Interim => Entry.Interim;
}

/// <summary>
/// Finds the wake-word models and verifies their SHA-256 against the manifest.
/// Folders searched, first with a <c>wakeword-models.json</c> wins:
/// <c>ALICEVOICE_WAKEWORD_DIR</c> / <c>WakeWordDir</c>,
/// <c>%ProgramData%\AliceVoice\wakeword</c>, <c>models\wakeword</c> next to the exe,
/// then <c>models\wakeword</c> in a parent folder (repo checkout).
/// The model is <c>ALICEVOICE_WAKEWORD_MODEL</c> / <c>WakeWordModel</c>, else the manifest default.
/// </summary>
public static class WakeWordModelLocator
{
    public const string ManifestName = "wakeword-models.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEnumerable<string> CandidateDirectories(string? configuredDir, string? sharedDataDir, string? baseDir = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir)) yield return configuredDir;
        if (!string.IsNullOrWhiteSpace(sharedDataDir)) yield return Path.Combine(sharedDataDir, "wakeword");
        var dir = new DirectoryInfo(baseDir ?? AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "models", "wakeword");
    }

    public static string? FindManifestDirectory(IEnumerable<string> candidates)
        => candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestName)));

    public static WakeWordManifest LoadManifest(string dir)
        => JsonSerializer.Deserialize<WakeWordManifest>(File.ReadAllText(Path.Combine(dir, ManifestName)), Json)
           ?? throw new InvalidDataException("Empty wake-word manifest");

    /// <summary>Locate + verify. Returns null with a human-readable <paramref name="reason"/> when unavailable.</summary>
    public static WakeWordModelInfo? TryResolve(string? configuredDir, string? sharedDataDir, string? modelId,
        out string reason, string? baseDir = null)
    {
        var dir = FindManifestDirectory(CandidateDirectories(configuredDir, sharedDataDir, baseDir));
        if (dir is null)
        {
            reason = $"No wake-word model installed ({ManifestName} not found in the models folder or %ProgramData%\\AliceVoice\\wakeword).";
            return null;
        }
        return TryResolveIn(dir, modelId, out reason);
    }

    public static WakeWordModelInfo? TryResolveIn(string dir, string? modelId, out string reason)
    {
        WakeWordManifest m;
        try { m = LoadManifest(dir); }
        catch (Exception ex) { reason = $"Wake-word manifest unreadable: {ex.Message}"; return null; }

        var id = string.IsNullOrWhiteSpace(modelId) ? m.Default : modelId;
        var entry = m.Models.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { reason = $"Wake-word model '{id}' is not listed in {Path.Combine(dir, ManifestName)}."; return null; }

        string Full(string f) => Path.Combine(dir, f);
        foreach (var (file, sha) in new[] { (m.Melspectrogram.File, m.Melspectrogram.Sha256), (m.Embedding.File, m.Embedding.Sha256), (entry.File, entry.Sha256) })
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(Full(file))) { reason = $"Wake-word file missing: {Full(file)}"; return null; }
            if (!VerifySha256(Full(file), sha)) { reason = $"Wake-word file {file} does not match its SHA-256 in the manifest; refusing to load it."; return null; }
        }
        reason = "";
        return new WakeWordModelInfo(dir, Full(m.Melspectrogram.File), Full(m.Embedding.File), Full(entry.File), entry);
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static bool VerifySha256(string path, string expected)
        => expected.Length == 64 && string.Equals(Sha256Of(path), expected, StringComparison.OrdinalIgnoreCase);
}
