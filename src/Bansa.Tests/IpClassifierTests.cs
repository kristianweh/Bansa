using Bansa.Services;
using Xunit;

namespace Bansa.Tests;

public class IpClassifierTests
{
    [Theory]
    // Loopback
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    // RFC-1918 private
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    // Just OUTSIDE the 172.16/12 block
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    // Link-local
    [InlineData("169.254.10.10", true)]
    // CGNAT (RFC 6598) — Tailscale/ZeroTier/ISP CGN
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.0.1", false)]
    [InlineData("100.128.0.1", false)]
    // Multicast / broadcast / reserved
    [InlineData("224.0.0.251", true)]
    [InlineData("239.255.255.250", true)]
    [InlineData("255.255.255.255", true)]
    // Public IPv4
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    // IPv4-mapped IPv6 unwraps to the IPv4 rules
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    // IPv6 scopes
    [InlineData("fe80::1", true)]                  // link-local
    [InlineData("fd00::1", true)]                  // unique-local (ULA)
    [InlineData("fc00::1", true)]                  // ULA lower block
    [InlineData("ff02::fb", true)]                 // multicast (mDNS)
    [InlineData("2607:f8b0:4004:800::200e", false)] // public (Google)
    public void ClassifiesAddresses(string addr, bool expected)
        => Assert.Equal(expected, IpClassifier.IsLocal(addr));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("999.1.1.1")]
    public void UnparseableIsNotLocal(string? addr)
        => Assert.False(IpClassifier.IsLocal(addr));
}
