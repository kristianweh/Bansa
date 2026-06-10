using System;
using System.Net;

namespace Bansa.Services;

/// <summary>
/// Single source of truth for "is this address local?" — used by the ETW packet filter
/// (NetworkMonitor) and the per-row IsLocalOnly classification (MainViewModel), so both
/// layers agree on what counts as local. Covers loopback, link-local, RFC-1918,
/// CGNAT (RFC 6598), multicast/reserved, and the IPv6 ULA/link-local/multicast scopes.
/// </summary>
public static class IpClassifier
{
    public static bool IsLocal(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;

        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:192.168.1.1) so IPv4 byte checks apply.
        var ip = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
        var b  = ip.GetAddressBytes();

        if (b.Length == 4)
        {
            if (b[0] == 10) return true;                                   // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16-31.0/12
            if (b[0] == 192 && b[1] == 168) return true;                   // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;                   // 169.254.0.0/16 link-local
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64.0.0/10  CGNAT (RFC 6598) — many ISPs
            if (b[0] >= 224) return true;                                  // 224.0.0.0/4 multicast + 240+ reserved
            //   (covers mDNS 224.0.0.251, SSDP 239.255.255.250, broadcast 255.255.255.255)
        }
        else if (b.Length == 16)
        {
            // fe80::/10 — IPv6 link-local (never routed beyond the local segment)
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
            // fc00::/7  — IPv6 unique-local (RFC 4193; covers fc00:: and fd00:: blocks)
            if ((b[0] & 0xfe) == 0xfc) return true;
            // ff00::/8  — IPv6 multicast (mDNS ff02::fb, all-nodes ff02::1, etc.)
            if (b[0] == 0xff) return true;
        }

        return false;
    }

    /// <summary>String overload for connection RemoteAddress values. Unparseable → false.</summary>
    public static bool IsLocal(string? addr)
        => !string.IsNullOrEmpty(addr) && IPAddress.TryParse(addr, out var ip) && IsLocal(ip);
}
