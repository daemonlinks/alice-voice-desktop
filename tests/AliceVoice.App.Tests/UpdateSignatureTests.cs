using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AliceVoice.Core.Updates;
using Xunit;

namespace AliceVoice.App.Tests;

public sealed class UpdateSignatureTests
{
    private static readonly X509Certificate2 Root = TestPki.CreateRoot("AliceVoice Test Root");
    private static readonly X509Certificate2 Signer = TestPki.CreateLeaf(Root, "AliceVoice Test Signer");
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("""{"version":"9.9.9","downloadUrl":"https://example.test/a.msix","sha256":"00"}""");

    [Fact]
    public void ValidSignaturePasses()
    {
        var sig = UpdateSigner.SignDetached(Manifest, Signer);
        var r = TestPki.Verifier(Root).Verify(Manifest, sig);
        Assert.True(r.IsValid, r.Reason);
        Assert.Equal(Signer.Thumbprint, r.SignerThumbprint);
    }

    [Fact]
    public void UnreachableRevocationIsToleratedAndReported()
    {
        // Test certificates have no CRL distribution point, so revocation cannot be checked (offline case).
        var r = TestPki.Verifier(Root).Verify(Manifest, UpdateSigner.SignDetached(Manifest, Signer));
        Assert.True(r.IsValid, r.Reason);
        Assert.False(r.RevocationChecked);
    }

    [Fact]
    public void TamperedContentFails()
    {
        var sig = UpdateSigner.SignDetached(Manifest, Signer);
        var tampered = (byte[])Manifest.Clone();
        tampered[^3] ^= 0x01;
        var r = TestPki.Verifier(Root).Verify(tampered, sig);
        Assert.False(r.IsValid);
        Assert.Contains("does not match", r.Reason);
    }

    [Fact]
    public void TamperedSignatureFails()
    {
        var sig = UpdateSigner.SignDetached(Manifest, Signer);
        sig[^5] ^= 0xFF;
        Assert.False(TestPki.Verifier(Root).Verify(Manifest, sig).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void UnsignedFails(byte[]? sig)
    {
        var r = TestPki.Verifier(Root).Verify(Manifest, sig);
        Assert.False(r.IsValid);
        Assert.StartsWith("unsigned", r.Reason);
    }

    [Fact]
    public void GarbageSignatureFails() =>
        Assert.False(TestPki.Verifier(Root).Verify(Manifest, Encoding.UTF8.GetBytes("not a signature")).IsValid);

    [Fact]
    public void WrongIssuerFails()
    {
        var otherRoot = TestPki.CreateRoot("Attacker Root");
        var otherSigner = TestPki.CreateLeaf(otherRoot, "AliceVoice Test Signer");
        var sig = UpdateSigner.SignDetached(Manifest, otherSigner, new X509Certificate2Collection(otherRoot));
        var r = TestPki.Verifier(Root).Verify(Manifest, sig);
        Assert.False(r.IsValid);
        Assert.Contains("chain", r.Reason);
    }

    [Fact]
    public void SelfSignedSignerFails()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=AliceVoice Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(UpdateSignatureVerifier.CodeSigningOid) }, false));
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.False(TestPki.Verifier(Root).Verify(Manifest, UpdateSigner.SignDetached(Manifest, self)).IsValid);
    }

    [Theory]
    [InlineData("1.3.6.1.5.5.7.3.1")] // server auth
    [InlineData(null)]                // no EKU extension at all
    public void MissingCodeSigningEkuFails(string? eku)
    {
        var leaf = TestPki.CreateLeaf(Root, "Not A Code Signer", eku);
        var r = TestPki.Verifier(Root).Verify(Manifest, UpdateSigner.SignDetached(Manifest, leaf));
        Assert.False(r.IsValid);
        Assert.Contains("Code Signing EKU", r.Reason);
    }

    [Fact]
    public void KeyUsageWithoutDigitalSignatureFails()
    {
        var leaf = TestPki.CreateLeaf(Root, "Encipher Only", usage: X509KeyUsageFlags.KeyEncipherment);
        Assert.False(TestPki.Verifier(Root).Verify(Manifest, UpdateSigner.SignDetached(Manifest, leaf)).IsValid);
    }

    [Fact]
    public void RootNotMatchingPinFails()
    {
        var r = TestPki.Verifier(Root, pin: UpdateSignatureVerifier.ProductionRootThumbprint)
            .Verify(Manifest, UpdateSigner.SignDetached(Manifest, Signer));
        Assert.False(r.IsValid);
        Assert.Contains("pinned", r.Reason);
    }

    [Fact]
    public void ExpiredSignerFails()
    {
        var r = TestPki.Verifier(Root, now: () => DateTime.Now.AddYears(3))
            .Verify(Manifest, UpdateSigner.SignDetached(Manifest, Signer));
        Assert.False(r.IsValid);
    }

    [Fact]
    public void TestSignatureFailsAgainstProductionRoot() =>
        Assert.False(UpdateSignatureVerifier.CreateDefault().Verify(Manifest, UpdateSigner.SignDetached(Manifest, Signer)).IsValid);

    [Fact]
    public void ProductionRootIsThePinnedDaemonlinksCa()
    {
        using var root = UpdateSignatureVerifier.LoadProductionRoot();
        Assert.Equal(UpdateSignatureVerifier.ProductionRootThumbprint, root.Thumbprint);
        Assert.StartsWith("CN=daemonlinks-AHC-CA", root.Subject);
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown, true)]
    [InlineData(X509ChainStatusFlags.OfflineRevocation, true)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation, true)]
    [InlineData(X509ChainStatusFlags.Revoked, false)]
    [InlineData(X509ChainStatusFlags.Revoked | X509ChainStatusFlags.RevocationStatusUnknown, false)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot, false)]
    [InlineData(X509ChainStatusFlags.NotTimeValid, false)]
    [InlineData(X509ChainStatusFlags.NoError, false)]
    public void OnlyUnreachableRevocationIsTolerated(X509ChainStatusFlags status, bool tolerated) =>
        Assert.Equal(tolerated, UpdateSignatureVerifier.IsOfflineTolerated(status));

    [Fact]
    public void SignatureUrlAppendsP7sToPath() =>
        Assert.Equal("https://example.test/x/version.json.p7s?a=1",
            HttpAppUpdater.SignatureUrlFor("https://example.test/x/version.json?a=1"));
}