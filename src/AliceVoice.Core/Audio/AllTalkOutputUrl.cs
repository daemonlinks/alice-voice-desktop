using System.Net;

namespace AliceVoice.Core.Audio;

/// <summary>
/// Turns AllTalk's <c>output_file_url</c> into a URL the client can fetch.
/// The server often returns an absolute loopback address
/// (<c>127.0.0.1</c>, <c>localhost</c>, <c>::1</c>) even when the generate
/// call went to another host. A remote phone or PC cannot download that.
/// The path stays; the host becomes the AllTalk base this client used.
/// </summary>
public static class AllTalkOutputUrl
{
    public static string Resolve(string baseUrl, string outputFileUrl)
    {
        var root = (baseUrl ?? "").Trim().TrimEnd('/');
        var raw = (outputFileUrl ?? "").Trim();
        if (raw.Length == 0) return raw;
        // A leading slash is a server path ("/audio/x.wav"). On Linux that
        // string is also an absolute file path, so UriKind.Absolute is not
        // enough to tell the two apart. Only http(s) is an AllTalk URL.
        if (!TryHttp(raw, out var absolute))
            return root + (raw.StartsWith('/') ? "" : "/") + raw;
        if (!IsLoopback(absolute)) return raw;

        var basis = new Uri(root + "/");
        var rewritten = new UriBuilder(absolute)
        {
            Scheme = basis.Scheme,
            Host = basis.Host,
            Port = basis.IsDefaultPort ? -1 : basis.Port
        };
        return rewritten.Uri.AbsoluteUri;
    }

    private static bool TryHttp(string raw, out Uri absolute)
    {
        absolute = null!;
        if (!raw.Contains("://", StringComparison.Ordinal)) return false;
        return Uri.TryCreate(raw, UriKind.Absolute, out absolute!)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsLoopback(Uri uri)
    {
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var bare = host.Trim('[', ']');
        return IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address);
    }
}
