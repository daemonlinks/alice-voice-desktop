using System.Net;
using System.Net.Sockets;

namespace AliceVoice.Universal.Host;

/// <summary>
/// The bridge listens on the Beast's Tailscale address unless the owner sets
/// <c>ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND=1</c>. Tailscale's CGNAT range is
/// 100.64.0.0/10, and its IPv6 range is fd7a:115c:a1e0::/48. MagicDNS names
/// (a single label, or anything ending in <c>.ts.net</c>) are treated as Tailscale.
/// </summary>
public static class BridgeBindPolicy
{
    public const string OverrideEnvironmentVariable = "ALICEVOICE_BRIDGE_ALLOW_UNSAFE_BIND";

    /// <summary>Tailscale MagicDNS name of the Beast. The phone uses this when <c>ALICEVOICE_BEAST_HOST</c> is unset.</summary>
    public const string DefaultBeastHost = "ahc-the-beast";

    public static bool IsOverrideEnabled(string? value)
        => value is "1" or "true" or "TRUE" or "yes" or "YES";

    public static BridgeBindDecision Evaluate(string host, bool allowUnsafe)
    {
        if (IsTailscale(host))
            return new BridgeBindDecision(true, false, "Tailscale address");
        var reason = RefusalReason(host);
        if (allowUnsafe)
            return new BridgeBindDecision(true, true, reason + " Allowed only because " + OverrideEnvironmentVariable + " is set.");
        return new BridgeBindDecision(false, false, reason + " Set " + OverrideEnvironmentVariable + "=1 to listen anyway.");
    }

    public static bool IsTailscale(string host)
    {
        var name = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(name, out var ip))
            return IsTailscaleIp(ip);
        if (name.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            return true;
        // MagicDNS machine name: one label, not an IP.
        return name.Length > 0 && !name.Contains('.') && !name.Contains(':') && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTailscaleIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
        }
        return false;
    }

    private static string RefusalReason(string host)
    {
        var name = host.Trim();
        if (name is "0.0.0.0" or "::" or "[::]")
            return $"Refusing bridge bind {name}: that is not a Tailscale address; it listens on every interface.";
        if (IPAddress.TryParse(name, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168)
                return $"Refusing bridge bind {name}: 192.168.x is a LAN address, not Tailscale.";
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || b[0] == 127)
                return $"Refusing bridge bind {name}: it is not a Tailscale address.";
            return $"Refusing bridge bind {name}: public addresses are not accepted.";
        }
        return $"Refusing bridge bind {name}: it is not a Tailscale address (100.64.0.0/10, fd7a:115c:a1e0::/48, or a MagicDNS name).";
    }
}

public sealed record BridgeBindDecision(bool Allowed, bool Warned, string Reason);
