using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AliceVoice.Core.Updates;

// Release-side signing for the self-update manifest (version.json).
// The signing key stays in the Windows certificate store (non-exportable); this tool only asks the
// store to sign. Usage:
//   sign   --thumbprint <T> --file version.json [--out version.json.p7s] [--store CurrentUser|LocalMachine]
//   verify --file version.json [--sig version.json.p7s]
return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }
    var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
    try
    {
        switch (args[0])
        {
            case "sign":
            {
                var file = Need(opts, "file");
                var thumb = Need(opts, "thumbprint").Replace(" ", "", StringComparison.Ordinal);
                var outPath = opts.GetValueOrDefault("out") ?? file + ".p7s";
                var loc = Enum.Parse<StoreLocation>(opts.GetValueOrDefault("store") ?? "CurrentUser", true);
                using var cert = UpdateSigner.FindCertificate(thumb, loc);
                var content = ToLf(File.ReadAllBytes(file), out var normalized);
                if (normalized)
                {
                    // git stores version.json with LF and GitHub serves those bytes; sign exactly that.
                    File.WriteAllBytes(file, content);
                    Console.WriteLine($"Normalized {file} to LF line endings (the bytes GitHub serves).");
                }
                var sig = UpdateSigner.SignDetached(content, cert);
                File.WriteAllBytes(outPath, sig);
                Console.WriteLine($"Signed {file} ({content.Length} bytes, sha256 {Convert.ToHexStringLower(SHA256.HashData(content))})");
                Console.WriteLine($"  signer {cert.Subject} [{cert.Thumbprint}], expires {cert.NotAfter:yyyy-MM-dd}");
                Console.WriteLine($"  -> {outPath}");
                return Verify(content, sig);
            }
            case "verify":
            {
                var file = Need(opts, "file");
                var sigPath = opts.GetValueOrDefault("sig") ?? file + ".p7s";
                var content = ToLf(File.ReadAllBytes(file), out var normalized);
                if (normalized) Console.WriteLine("Note: file has CRLF line endings (git checkout); verifying the LF form that is committed and served.");
                return Verify(content, File.Exists(sigPath) ? File.ReadAllBytes(sigPath) : null);
            }
            default:
                Usage();
                return 2;
        }
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidOperationException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

static int Verify(byte[] content, byte[]? sig)
{
    var r = UpdateSignatureVerifier.CreateDefault().Verify(content, sig);
    Console.WriteLine(r.IsValid
        ? $"VERIFIED against pinned daemonlinks-AHC-CA: {r.Reason} (revocation checked: {r.RevocationChecked})"
        : $"REJECTED: {r.Reason}");
    return r.IsValid ? 0 : 3;
}

static byte[] ToLf(byte[] b, out bool changed)
{
    var o = new List<byte>(b.Length);
    for (var i = 0; i < b.Length; i++)
        if (!(b[i] == (byte)'\r' && i + 1 < b.Length && b[i + 1] == (byte)'\n')) o.Add(b[i]);
    changed = o.Count != b.Length;
    return changed ? o.ToArray() : b;
}

static string Need(Dictionary<string, string> o, string k) =>
    o.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"--{k} is required");

static void Usage() => Console.WriteLine("""
    AliceVoice.ReleaseSigner
      sign   --thumbprint <T> --file version.json [--out version.json.p7s] [--store CurrentUser|LocalMachine]
      verify --file version.json [--sig version.json.p7s]
    """);