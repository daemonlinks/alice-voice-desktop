using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace AliceVoice.Core.Updates;

/// <summary>Outcome of a release signature check. <see cref="Reason"/> is safe to log.</summary>
public sealed record SignatureCheck(
    bool IsValid,
    string Reason,
    string? SignerThumbprint = null,
    string? SignerSubject = null,
    bool RevocationChecked = false)
{
    public static SignatureCheck Fail(string reason) => new(false, reason);
}

/// <summary>
/// Verifies a detached CMS/PKCS#7 signature over release content (the update
/// manifest). A signature is accepted only if: it decodes as a detached CMS
/// with exactly one signer; the signature matches the content; the signer
/// certificate has the Code Signing EKU (1.3.6.1.5.5.7.3.3) and, if present,
/// the digitalSignature key usage; and the chain builds to the pinned root
/// (CustomRootTrust with only that root trusted, root thumbprint compared
/// explicitly), valid at the verification time.
/// Revocation is offline-tolerant: the CA publishes its CRL only over LDAP
/// inside the domain, so if the CRL cannot be fetched (RevocationStatusUnknown /
/// OfflineRevocation) the signature is still accepted and the result says
/// revocation was not checked; a certificate that is known to be revoked, or
/// any other chain error, is always rejected.
/// </summary>
public sealed class UpdateSignatureVerifier
{
    public const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    /// <summary>SHA-1 thumbprint of the daemonlinks-AHC-CA root (AHC-CORE ADCS), valid until 2031-05-08.</summary>
    public const string ProductionRootThumbprint = "7ED67FFF3DF7360AA24E4DE1C01B300BFEBAFF0B";

    /// <summary>Public certificate (DER, base64) of CN=daemonlinks-AHC-CA, DC=daemonlinks, DC=internal.</summary>
    private const string ProductionRootBase64 =
        "MIIDgzCCAmugAwIBAgIQNQkkP3GD04lHkRCuNKtTIjANBgkqhkiG9w0BAQsFADBUMRgwFgYKCZImiZPyLGQBGRYIaW50ZXJuYWwxGzAZBgoJkiaJk/IsZAEZFgtkYWVtb25saW5rczEbMBkGA1UEAxMSZGFlbW9ubGlua3MtQUhDLUNBMB4XDTI2MDUwNzIzMjA0M1oXDTMxMDUwNzIzMzA0M1owVDEYMBYGCgmSJomT8ixkARkWCGludGVybmFsMRswGQYKCZImiZPyLGQBGRYLZGFlbW9ubGlua3MxGzAZBgNVBAMTEmRhZW1vbmxpbmtzLUFIQy1DQTCCASIwDQYJKoZIhvcNAQEBBQADggEPADCCAQoCggEBALa1nfPmPWOeEc/5rAVo+pQNWqZObAJTEUNb8ZboD3+DjYubMbYBBBumNg86/n5MuvxYR4DkLdusGukiEyI5honnRajNGmKbc0IL6xvqoOwoZpyVOnVezsvNP1BnczjzLHsepVIlmB1PfTn+eiIkrxnaRKg9e2pmxqauNQlRLtYvoTvpiazQiMxvFapGdELJhwqCp9m7HWOuRevI4aOMAMi7EaIM6EW11zQGdQtS2Edvu9AeNVHiowGSY0tI2RilufiBRnLsPro0W5LvieJKI2mEnpCFSlnTFcM1qPHsj/Rcntjsa+qSIjvmXzMPVIbNVC7TbO0DJ03ciNdxEQ57/s0CAwEAAaNRME8wCwYDVR0PBAQDAgGGMA8GA1UdEwEB/wQFMAMBAf8wHQYDVR0OBBYEFPFz2S5236hxQVy1bZ7MBaUXeDciMBAGCSsGAQQBgjcVAQQDAgEAMA0GCSqGSIb3DQEBCwUAA4IBAQAHW3oGxyAnOn8CkUnANfoghpgpDxObUmPaLOgpFyy0UPKTExy9pKjwsuUkB2eRANjS3ZnUuyLPd6c7WbtV+0CbtLQbJehqLcOPmd1vyJcU+lWkYSMu4HibC2ci8su17GMfq7LQWuJRvFbWqZU1U5ZeDaH6ZRS9J0b0dcpsraeiZA4XHkBCcw4TaObBTHmxKhBnZhKXV+wk9xGaMB1bzIHqf3cjX5BUpATam55/bL3I/pwv+rVu2C+mJLbIoasKArqGsUpM29RJ74yNy6LEqydWSDxa7a3xPOI4gp20c0+1IP8MvC0rOctl/N7iuDiBjSQVI26APjuCy/yGPU6K9mkF";

    private const X509ChainStatusFlags OfflineTolerated =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    private readonly X509Certificate2 _root;
    private readonly string _pinnedRootThumbprint;
    private readonly X509RevocationMode _revocationMode;
    private readonly TimeSpan _revocationTimeout;
    private readonly Func<DateTime> _now;

    public UpdateSignatureVerifier(X509Certificate2 trustedRoot, string pinnedRootThumbprint,
        X509RevocationMode revocationMode = X509RevocationMode.Online, TimeSpan? revocationTimeout = null,
        Func<DateTime>? now = null)
    {
        _root = trustedRoot ?? throw new ArgumentNullException(nameof(trustedRoot));
        _pinnedRootThumbprint = pinnedRootThumbprint ?? throw new ArgumentNullException(nameof(pinnedRootThumbprint));
        _revocationMode = revocationMode;
        _revocationTimeout = revocationTimeout ?? TimeSpan.FromSeconds(10);
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Verifier for production releases: trusts only the pinned daemonlinks-AHC-CA root.</summary>
    public static UpdateSignatureVerifier CreateDefault() =>
        new(LoadProductionRoot(), ProductionRootThumbprint);

    public static X509Certificate2 LoadProductionRoot() =>
        X509CertificateLoader.LoadCertificate(Convert.FromBase64String(ProductionRootBase64));

    /// <summary>True for chain status flags that only mean "revocation could not be checked".</summary>
    public static bool IsOfflineTolerated(X509ChainStatusFlags status) =>
        status != X509ChainStatusFlags.NoError && (status & ~OfflineTolerated) == 0;

    public SignatureCheck Verify(byte[] content, byte[]? signature)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (signature is null || signature.Length == 0) return SignatureCheck.Fail("unsigned: no signature");
        if (!ThumbprintEquals(_root.Thumbprint, _pinnedRootThumbprint))
            return SignatureCheck.Fail("configured root does not match the pinned root thumbprint");

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        try { cms.Decode(signature); }
        catch (CryptographicException) { return SignatureCheck.Fail("signature is not a valid CMS/PKCS#7 blob"); }

        if (cms.SignerInfos.Count != 1) return SignatureCheck.Fail($"expected exactly one signer, found {cms.SignerInfos.Count}");
        var signer = cms.SignerInfos[0];
        var cert = signer.Certificate;
        if (cert is null) return SignatureCheck.Fail("signer certificate is not included in the signature");

        try { signer.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { return SignatureCheck.Fail("signature does not match the content"); }

        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigningOid))
            return SignatureCheck.Fail("signer certificate lacks the Code Signing EKU");
        var ku = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (ku is not null && (ku.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
            return SignatureCheck.Fail("signer certificate key usage does not allow digital signatures");

        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Add(_root);
        foreach (var extra in cms.Certificates)
            if (!ThumbprintEquals(extra.Thumbprint, cert.Thumbprint)) policy.ExtraStore.Add(extra);
        policy.ApplicationPolicy.Add(new Oid(CodeSigningOid));
        policy.RevocationMode = _revocationMode;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.UrlRetrievalTimeout = _revocationTimeout;
        policy.DisableCertificateDownloads = true;
        policy.VerificationTime = _now();

        chain.Build(cert);
        var revocationChecked = _revocationMode != X509RevocationMode.NoCheck;
        foreach (var status in chain.ChainStatus)
        {
            if (status.Status == X509ChainStatusFlags.NoError) continue;
            if (IsOfflineTolerated(status.Status)) { revocationChecked = false; continue; }
            return SignatureCheck.Fail($"certificate chain is not valid: {status.Status}");
        }

        var elements = chain.ChainElements;
        if (elements.Count == 0) return SignatureCheck.Fail("certificate chain is empty");
        var top = elements[elements.Count - 1].Certificate;
        if (!ThumbprintEquals(top.Thumbprint, _pinnedRootThumbprint))
            return SignatureCheck.Fail("certificate chain does not end at the pinned root");

        return new SignatureCheck(true,
            revocationChecked ? "valid" : "valid (revocation not checked: CRL unreachable)",
            cert.Thumbprint, cert.Subject, revocationChecked);
    }

    private static bool ThumbprintEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Release-side helper: creates the detached CMS signature that <see cref="UpdateSignatureVerifier"/> checks.</summary>
public static class UpdateSigner
{
    /// <summary>
    /// Signs <paramref name="content"/> (SHA-256, signer identified by issuer and serial,
    /// signer certificate included, signing time attribute). The private key is used in
    /// place via the certificate's key handle and is never exported.
    /// </summary>
    public static byte[] SignDetached(byte[] content, X509Certificate2 signerCert, X509Certificate2Collection? extraCerts = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signerCert);
        if (!signerCert.HasPrivateKey) throw new InvalidOperationException("The signing certificate has no private key.");

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signerCert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        if (extraCerts is not null) signer.Certificates.AddRange(extraCerts);
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    /// <summary>Finds a code signing certificate with a private key by thumbprint in the given store (default CurrentUser\My).</summary>
    public static X509Certificate2 FindCertificate(string thumbprint, StoreLocation location = StoreLocation.CurrentUser)
    {
        var tp = thumbprint.Replace(" ", "", StringComparison.Ordinal).Trim();
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, tp, validOnly: false);
        var cert = found.Count == 1 ? found[0] : throw new InvalidOperationException($"No unique certificate {tp} in {location}\\My.");
        if (!cert.HasPrivateKey) throw new InvalidOperationException($"Certificate {tp} has no private key in this store.");
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == UpdateSignatureVerifier.CodeSigningOid))
            throw new InvalidOperationException($"Certificate {tp} does not have the Code Signing EKU.");
        return cert;
    }
}