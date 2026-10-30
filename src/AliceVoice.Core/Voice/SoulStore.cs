using System.Text;
using AliceVoice.Core.Config;

namespace AliceVoice.Core.Voice;

/// <summary>What the SOUL editor shows.</summary>
/// <param name="Text">SOUL text (empty if none exists yet).</param>
/// <param name="SourcePath">File the text was read from, or null.</param>
/// <param name="IsLocal">True if read from the Alice LOCAL SOUL; false if pre-filled from the read-only fallback.</param>
/// <param name="TargetPath">The only file Save will ever write (the Alice LOCAL SOUL).</param>
public sealed record SoulDocument(string Text, string? SourcePath, bool IsLocal, string TargetPath);

/// <summary>Result of a save: the file written and the backup taken (null if there was nothing to back up).</summary>
public sealed record SoulSaveResult(string Path, string? BackupPath);

/// <summary>
/// Loads and saves the Alice LOCAL SOUL for the editor. Saving only ever
/// writes <see cref="AliceVoiceOptions.ResolvedAliceLocalSoulPath"/>
/// (checked with <see cref="SoulResolver.IsWritable"/>), never the Hermes
/// alice_voice profile SOUL. Before overwriting, the current file is copied to
/// a timestamped backup in a <c>backups</c> folder beside it, and the new text
/// is written atomically (temp file + replace).
/// </summary>
public sealed class SoulStore
{
    private readonly AliceVoiceOptions _options;
    private readonly Func<DateTime> _now;

    public SoulStore(AliceVoiceOptions options, Func<DateTime>? now = null)
    {
        _options = options;
        _now = now ?? (() => DateTime.Now);
    }

    public string TargetPath => _options.ResolvedAliceLocalSoulPath;
    public string BackupDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(TargetPath))!, "backups");

    public async Task<SoulDocument> LoadAsync(CancellationToken ct = default)
    {
        var path = SoulResolver.ReadPath(_options);
        if (path is null) return new SoulDocument("", null, false, TargetPath);
        var text = await File.ReadAllTextAsync(path, ct);
        var isLocal = string.Equals(Path.GetFullPath(path), Path.GetFullPath(TargetPath), StringComparison.OrdinalIgnoreCase);
        return new SoulDocument(text, path, isLocal, TargetPath);
    }

    public async Task<SoulSaveResult> SaveAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Refusing to save an empty SOUL.");
        var target = Path.GetFullPath(TargetPath);
        if (!SoulResolver.IsWritable(_options, target))
            throw new UnauthorizedAccessException($"The SOUL editor may only write the Alice LOCAL SOUL; {target} is protected (it is the fallback or inside a Hermes profile).");

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

        var tmp = target + ".tmp";
        await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct);
        File.Move(tmp, target, overwrite: true);
        return new SoulSaveResult(target, backup);
    }
}
