using IoTSpy.Core.Models;
using IoTSpy.Scanner;
using Xunit;

namespace IoTSpy.Scanner.Tests;

public class CidrHelperTests
{
    // ── IPv4 containment ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0")]
    [InlineData("192.168.1.0/24", "192.168.1.1")]
    [InlineData("192.168.1.0/24", "192.168.1.255")]
    [InlineData("10.0.0.0/8",     "10.255.255.255")]
    [InlineData("10.0.0.0/8",     "10.0.0.1")]
    [InlineData("192.168.1.50/32","192.168.1.50")]
    public void Contains_IPv4_ReturnsTrueForAddressInRange(string cidr, string ip)
        => Assert.True(CidrHelper.Contains(cidr, ip));

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.2.0")]
    [InlineData("192.168.1.0/24", "10.0.0.1")]
    [InlineData("192.168.1.50/32","192.168.1.51")]
    [InlineData("10.0.0.0/8",     "11.0.0.0")]
    public void Contains_IPv4_ReturnsFalseForAddressOutOfRange(string cidr, string ip)
        => Assert.False(CidrHelper.Contains(cidr, ip));

    // ── Bare IP (no slash) treated as /32 ─────────────────────────────────────

    [Fact]
    public void Contains_BareIpv4_MatchesExactAddress()
        => Assert.True(CidrHelper.Contains("10.0.0.1", "10.0.0.1"));

    [Fact]
    public void Contains_BareIpv4_DoesNotMatchOtherAddress()
        => Assert.False(CidrHelper.Contains("10.0.0.1", "10.0.0.2"));

    // ── IPv6 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Contains_IPv6_ReturnsTrueForAddressInRange()
        => Assert.True(CidrHelper.Contains("2001:db8::/32", "2001:db8::1"));

    [Fact]
    public void Contains_IPv6_ReturnsFalseForAddressOutOfRange()
        => Assert.False(CidrHelper.Contains("2001:db8::/32", "2001:db9::1"));

    // ── Family mismatch ───────────────────────────────────────────────────────

    [Fact]
    public void Contains_IPv4CidrWithIPv6Address_ReturnsFalse()
        => Assert.False(CidrHelper.Contains("192.168.1.0/24", "::1"));

    [Fact]
    public void Contains_IPv6CidrWithIPv4Address_ReturnsFalse()
        => Assert.False(CidrHelper.Contains("2001:db8::/32", "192.168.1.1"));

    // ── Malformed inputs ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("not-a-cidr", "192.168.1.1")]
    [InlineData("192.168.1.0/99", "192.168.1.1")]
    [InlineData("192.168.1.0/24", "not-an-ip")]
    [InlineData("", "192.168.1.1")]
    [InlineData("192.168.1.0/24", "")]
    public void Contains_InvalidInput_ReturnsFalse(string cidr, string ip)
        => Assert.False(CidrHelper.Contains(cidr, ip));

    // ── /0 catch-all ─────────────────────────────────────────────────────────

    [Fact]
    public void Contains_SlashZero_MatchesAllIPv4()
    {
        Assert.True(CidrHelper.Contains("0.0.0.0/0", "1.2.3.4"));
        Assert.True(CidrHelper.Contains("0.0.0.0/0", "255.255.255.255"));
    }

    // ── IsValidCidr ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.0/24")]
    [InlineData("10.0.0.5")]
    [InlineData("2001:db8::/32")]
    public void IsValidCidr_WellFormedInput_ReturnsTrue(string cidr)
        => Assert.True(CidrHelper.IsValidCidr(cidr));

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0/99")]
    [InlineData("")]
    public void IsValidCidr_MalformedInput_ReturnsFalse(string cidr)
        => Assert.False(CidrHelper.IsValidCidr(cidr));

    // ── IsInScope (the ScannerController/ScheduledScanService shared gate) ────

    [Fact]
    public void IsInScope_NoActiveScopes_ReturnsTrueForAnyDevice()
        => Assert.True(CidrHelper.IsInScope([], "203.0.113.5"));

    [Fact]
    public void IsInScope_ActiveScopeMatchesIp_ReturnsTrue()
    {
        var scopes = new List<ScanScope> { new() { Cidr = "10.0.0.0/24", IsActive = true } };
        Assert.True(CidrHelper.IsInScope(scopes, "10.0.0.5"));
    }

    [Fact]
    public void IsInScope_ActiveScopesConfiguredButNoneMatch_ReturnsFalse()
    {
        var scopes = new List<ScanScope> { new() { Cidr = "10.0.0.0/24", IsActive = true } };
        Assert.False(CidrHelper.IsInScope(scopes, "203.0.113.5"));
    }

    [Fact]
    public void IsInScope_MultipleScopes_MatchesIfAnyContains()
    {
        var scopes = new List<ScanScope>
        {
            new() { Cidr = "10.0.0.0/24", IsActive = true },
            new() { Cidr = "192.168.1.0/24", IsActive = true },
        };
        Assert.True(CidrHelper.IsInScope(scopes, "192.168.1.42"));
    }
}
