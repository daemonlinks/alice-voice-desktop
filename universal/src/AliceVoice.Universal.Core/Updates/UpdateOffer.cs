using AliceVoice.Core.Updates;
using AliceVoice.Universal.Host;

namespace AliceVoice.Universal.Updates;

/// <summary>
/// What the UI may say about a release that already passed the signed-manifest
/// checks. Neither head downloads or launches an update on its own — the WinUI
/// app only reports "Update available".
/// </summary>
public sealed record UpdateOffer(string Banner, bool CanInstall, string Reason)
{
    public static UpdateOffer For(ReleaseInfo release, PlatformHostKind platform)
    {
        if (platform == PlatformHostKind.Android && IsAndroidPackage(release.DownloadUrl))
        {
            return new UpdateOffer(
                $"Update available: {release.Version} (Android APK)",
                false,
                "The signed manifest names this APK. App U shows the offer and does not download or install it by itself.");
        }

        if (platform == PlatformHostKind.Android && IsWindowsPackage(release.DownloadUrl))
        {
            return new UpdateOffer(
                $"Update {release.Version} is signed, but the manifest download is a Windows package. Android will not install it.",
                false,
                "version.json has no android entry yet. Add android.version, android.downloadUrl, and android.sha256 on the Beast and re-sign version.json.p7s.");
        }

        return new UpdateOffer(
            $"Update available: {release.Version}",
            false,
            "App U reports the update and does not download or launch it, matching the WinUI app.");
    }

    public static bool IsWindowsPackage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var path = u.AbsolutePath;
        return path.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAndroidPackage(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
}
