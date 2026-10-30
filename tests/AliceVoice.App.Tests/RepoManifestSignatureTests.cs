using System.Text;
using AliceVoice.Core.Updates;
using Xunit;

namespace AliceVoice.App.Tests;

/// <summary>
/// Guards the committed release manifest: version.json must always be accompanied by a
/// version.json.p7s that the production verifier (pinned daemonlinks-AHC-CA root) accepts.
/// If version.json is edited without re-signing, or the signing certificate expires, this fails.
/// </summary>
public sealed class RepoManifestSignatureTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "README.md")))
                return candidate;
        }
        throw new FileNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }

    // GitHub raw serves the committed (LF) bytes; a Windows checkout may have CRLF.
    private static byte[] AsServed(byte[] bytes) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));

    [Fact]
    public void CommittedManifestIsSignedByProductionPki()
    {
        var manifest = AsServed(File.ReadAllBytes(RepoFile("version.json")));
        var signature = File.ReadAllBytes(RepoFile("version.json.p7s"));

        var result = UpdateSignatureVerifier.CreateDefault().Verify(manifest, signature);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void SignatureSitsWhereTheUpdaterLooksForIt() =>
        Assert.Equal(
            "https://raw.githubusercontent.com/daemonlinks/alice-voice-desktop/main/version.json.p7s",
            HttpAppUpdater.SignatureUrlFor("https://raw.githubusercontent.com/daemonlinks/alice-voice-desktop/main/version.json"));
}