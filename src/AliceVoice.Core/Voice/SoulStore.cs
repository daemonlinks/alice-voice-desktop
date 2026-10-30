using System.Text;
using AliceVoice.Core.Config;

namespace AliceVoice.Core.Voice;

/// <summary>What the SOUL editor shows.</summary>
/// <param name="Text">SOUL text.</param>
/// <param name="Path">The Alice LOCAL SOUL file (the only file read or written).</param>
/// <param name="CreatedFromTemplate">True if the file did not exist and a neutral starter template was just created.</param>
public sealed record SoulDocument(string Text, string Path, bool CreatedFromTemplate);

/// <summary>Result of a save: the file written and the backup taken (null if there was nothing to back up).</summary>
public sealed record SoulSaveResult(string Path, string? BackupPath);

/// <summary>
/// Loads and saves the Alice LOCAL SOUL. Only
/// <see cref="AliceVoiceOptions.ResolvedAliceLocalSoulPath"/> is ever read or
/// written (checked with <see cref="SoulResolver"/>); no agent SOUL is used,
/// copied, or touched. If the local SOUL does not exist yet, a minimal neutral
/// starter template is created. Before overwriting, the current file is
/// copied to a timestamped backup in a <c>backups</c> folder beside it, and
/// the new text is written atomically (temp file + replace).
/// </summary>
public sealed class SoulStore
{
    /// <summary>Neutral starter SOUL written when Alice LOCAL has none. Contains no agent SOUL content.</summary>
    public const string StarterTemplate =
        "# Alice LOCAL - SOUL\n" +
        "\n" +
        "<!-- Starter template created by Alice Voice. This is Alice LOCAL's own SOUL,\n" +
        "     shared by the Alice Voice desktop app and web app. It is not any Hermes\n" +
        "     agent's SOUL. Edit it in the SOUL editor. -->\n" +
        "\n" +
        "You are Alice, a friendly voice assistant running locally on Master's PC.\n" +
        "\n" +
        "- Speak naturally and keep replies short; they are read aloud.\n" +
        "- Be honest when you do not know something.\n" +
        "- Use your tools (memory and web search) when they help.\n";

    private readonly AliceVoiceOptions _options;
    private readonly Func<DateTime> _now;

    public SoulStore(AliceVoiceOptions options, Func<DateTime>? now = null)
    {
        _options = options;
        _now = now ?? (() => DateTime.Now);
    }

    public string TargetPath => _options.ResolvedAliceLocalSoulPath;
    public string BackupDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(TargetPath))!, "backups");

    /// <summary>
    /// Read the Alice LOCAL SOUL, creating it from <see cref="StarterTemplate"/> if it does not exist.
    /// Throws <see cref="UnauthorizedAccessException"/> if the configured path is an agent SOUL.
    /// </summary>
    public async Task<SoulDocument> LoadAsync(CancellationToken ct = default)
    {
        var target = Path.GetFullPath(TargetPath);
        EnsureAllowed(target);
        if (File.Exists(target))
            return new SoulDocument(await File.ReadAllTextAsync(target, ct), target, false);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await WriteAtomicAsync(target, StarterTemplate, ct);
        return new SoulDocument(StarterTemplate, target, true);
    }

    public async Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Refusing to save an empty SOUL.");
        var target = Path.GetFullPath(TargetPath);
        EnsureAllowed(target);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string? backup = null;
        if (File.Exists(target))
        {
            Directory.CreateDirectory(BackupDir);
            var stamp = _now().ToString("yyyyMMdd-HHmmss-fff");
            backup = Path.Combine(BackupDir, $"SOUL.{stamp}.md");
            for (int i = 1; File.Exists(backup); i++) backup = Path.Combine(BackupDir, $"SOUL.{stamp}-{i}.md");
            File.Copy(target, backup);
        }

        await WriteAtomicAsync(target, text, ct);
        return new SoulSaveResult(target, backup);
    }

    private void EnsureAllowed(string target)
    {
        if (!SoulResolver.IsWritable(_options, target))
            throw new UnauthorizedAccessException($"Alice LOCAL may only use its own local SOUL; {target} is inside a Hermes agent profile and is never read or written.");
    }

    private static async Task WriteAtomicAsync(string target, string text, CancellationToken ct)
    {
        var tmp = target + ".tmp";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct);
        File.Move(tmp, target, overwrite: true);
    }
}