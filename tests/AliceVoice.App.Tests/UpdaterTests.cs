using System.Net;
using System.Security.Cryptography;
using System.Text;
using AliceVoice.Core.Updates;
using Xunit;

namespace AliceVoice.App.Tests;

public sealed class UpdaterTests : IDisposable
{
    private const string ManifestUrl = "https://example.test/version.json";
    private const string DownloadUrl = "https://example.test/AliceVoice.App.msix";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend installer bytes");
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "av_upd_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _launched = new();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Handler(string manifestJson, byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.RequestUri!.ToString() == ManifestUrl
                ? new ByteArrayContent(Encoding.UTF8.GetBytes(manifestJson))
                : new ByteArrayContent(payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }

    private HttpAppUpdater Updater(string version = "1.2.3", string? sha = null, string url = DownloadUrl, byte[]? payload = null, string current = "0.1.0")
    {
        var json = $$"""{"version":"{{version}}","downloadUrl":"{{url}}","sha256":"{{sha ?? PayloadSha}}"}""";
        return new HttpAppUpdater(ManifestUrl, _dir, new Handler(json, payload ?? Payload), current, p => _launched.Add(p));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v0.2", 0, 2, 0)]
    [InlineData("0.3.1-beta+abc", 0, 3, 1)]
    public void ParsesVersionsTolerantly(string s, int a, int b, int c) => Assert.Equal(new Version(a, b, c), HttpAppUpdater.ParseVersion(s));

    [Fact]
    public void UnparseableVersionIsNull() => Assert.Null(HttpAppUpdater.ParseVersion("latest"));

    [Fact]
    public async Task OffersNewerVerifiableRelease()
    {
        var r = await Updater().CheckForUpdateAsync();
        Assert.NotNull(r);
        Assert.Equal(PayloadSha, r!.Sha256);
    }

    [Fact]
    public async Task SameVersionIsNotOffered() => Assert.Null(await Updater(version: "0.1.0").CheckForUpdateAsync());

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    public async Task ReleaseWithoutValidHashIsNotOffered(string sha) => Assert.Null(await Updater(sha: sha).CheckForUpdateAsync());

    [Fact]
    public async Task NonHttpsReleaseIsNotOffered() => Assert.Null(await Updater(url: "http://example.test/x.msix").CheckForUpdateAsync());

    [Fact]
    public async Task VerifiedDownloadIsStagedAndLaunched()
    {
        var u = Updater();
        var r = (await u.CheckForUpdateAsync())!;
        var staged = await u.DownloadUpdateAsync(r);
        Assert.True(File.Exists(staged.Path));
        Assert.False(File.Exists(staged.Path + ".partial"));
        await u.ApplyUpdateAsync(staged);
        Assert.Equal(new[] { staged.Path }, _launched);
    }

    [Fact]
    public async Task HashMismatchDeletesDownloadAndNeverLaunches()
    {
        var u = Updater(payload: Encoding.UTF8.GetBytes("tampered"));
        var r = (await u.CheckForUpdateAsync())!;
        await Assert.ThrowsAsync<InvalidDataException>(() => u.DownloadUpdateAsync(r));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task FileChangedAfterVerificationIsNotLaunched()
    {
        var u = Updater();
        var staged = await u.DownloadUpdateAsync((await u.CheckForUpdateAsync())!);
        await File.WriteAllTextAsync(staged.Path, "swapped");
        await Assert.ThrowsAsync<InvalidDataException>(() => u.ApplyUpdateAsync(staged));
        Assert.False(File.Exists(staged.Path));
        Assert.Empty(_launched);
    }
}
