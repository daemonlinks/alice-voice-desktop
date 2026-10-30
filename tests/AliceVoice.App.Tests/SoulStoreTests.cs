using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

public sealed class SoulStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "av_soul_" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_dir, "alice_local", "SOUL.md");
    private string Fallback => Path.Combine(_dir, "hermes_profile", "SOUL.md");

    public SoulStoreTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Fallback)!);
        File.WriteAllText(Fallback, "FALLBACK SOUL");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SoulStore Store(DateTime? now = null) => new(
        new AliceVoiceOptions { AliceLocalSoulPath = Local, SoulFallbackPath = Fallback },
        () => now ?? new DateTime(2026, 10, 30, 17, 5, 9, 123));

    [Fact]
    public async Task LoadPrefillsFromFallbackButMarksItReadOnly()
    {
        var doc = await Store().LoadAsync();
        Assert.Equal("FALLBACK SOUL", doc.Text);
        Assert.False(doc.IsLocal);
        Assert.Equal(Path.GetFullPath(Local), Path.GetFullPath(doc.TargetPath));
    }

    [Fact]
    public async Task FirstSaveCreatesLocalSoulAndNeverTouchesFallback()
    {
        var r = await Store().SaveAsync("MY LOCAL SOUL");
        Assert.Null(r.BackupPath);
        Assert.Equal("MY LOCAL SOUL", File.ReadAllText(Local));
        Assert.Equal("FALLBACK SOUL", File.ReadAllText(Fallback));
        var doc = await Store().LoadAsync();
        Assert.True(doc.IsLocal);
        Assert.Equal("MY LOCAL SOUL", doc.Text);
    }

    [Fact]
    public async Task OverwriteTakesTimestampedBackupFirst()
    {
        await Store().SaveAsync("v1");
        var r = await Store().SaveAsync("v2");
        Assert.NotNull(r.BackupPath);
        Assert.Equal("SOUL.20261030-170509-123.md", Path.GetFileName(r.BackupPath));
        Assert.Equal("v1", File.ReadAllText(r.BackupPath!));
        Assert.Equal("v2", File.ReadAllText(Local));
        var r3 = await Store().SaveAsync("v3"); // same timestamp -> unique name
        Assert.NotEqual(r.BackupPath, r3.BackupPath);
        Assert.Equal("v2", File.ReadAllText(r3.BackupPath!));
        Assert.False(File.Exists(Local + ".tmp"));
    }

    [Fact]
    public async Task RefusesEmptyText()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().SaveAsync("   "));
        Assert.False(File.Exists(Local));
    }

    [Fact]
    public async Task RefusesWhenTargetIsTheFallback()
    {
        var store = new SoulStore(new AliceVoiceOptions { AliceLocalSoulPath = Fallback, SoulFallbackPath = Fallback });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync("overwrite"));
        Assert.Equal("FALLBACK SOUL", File.ReadAllText(Fallback));
    }

    [Fact]
    public async Task RefusesTargetsInsideHermesProfiles()
    {
        var o = new AliceVoiceOptions { SoulFallbackPath = Fallback };
        o.AliceLocalSoulPath = Path.Combine(o.HermesHome, "profiles", "alice_voice", "SOUL.md");
        // Throws before any I/O, so nothing under the real Hermes home is touched.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SoulStore(o).SaveAsync("overwrite"));
    }
}
