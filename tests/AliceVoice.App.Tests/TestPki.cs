using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AliceVoice.Core.Updates;

namespace AliceVoice.App.Tests;

/// <summary>Throwaway in-memory PKI for signature tests (never touches a certificate store).</summary>
internal static class TestPki
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static X509Certificate2 CreateRoot(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(5));
    }

    public static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string name,
        string? eku = UpdateSignatureVerifier.CodeSigningOid,
        X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        if (eku is not null)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var pub = req.Create(issuer, Now.AddHours(-1), notAfter ?? Now.AddYears(2), serial);
        return pub.CopyWithPrivateKey(key);
    }

    public static UpdateSignatureVerifier Verifier(X509Certificate2 root, string? pin = null, Func<DateTime>? now = null) =>
        new(root, pin ?? root.Thumbprint, now: now);
}