using IoTSpy.Proxy.Tls;
using Xunit;

namespace IoTSpy.Proxy.Tests;

public class DotDetectorTests
{
    private static ClientHelloInfo MakeInfo(string sni) => new() { SniHostname = sni };

    [Fact]
    public void IsLikelyDot_Port853_DetectedRegardlessOfSni()
    {
        var info = MakeInfo("some-unrelated-host.example.com");

        Assert.True(DotDetector.IsLikelyDot(info, 853));
    }

    [Fact]
    public void IsLikelyDot_KnownResolverSni_DetectedOnNonStandardPort()
    {
        var info = MakeInfo("dns.google");

        Assert.True(DotDetector.IsLikelyDot(info, 443));
    }

    [Theory]
    [InlineData("cloudflare-dns.com")]
    [InlineData("dns.quad9.net")]
    [InlineData("family.cloudflare-dns.com")]
    [InlineData("1dot1dot1dot1.cloudflare-dns.com")]
    [InlineData("dns.nextdns.io")]
    public void IsLikelyDot_OtherKnownResolvers_Detected(string sni)
    {
        var info = MakeInfo(sni);

        Assert.True(DotDetector.IsLikelyDot(info, 443));
    }

    [Fact]
    public void IsLikelyDot_UnknownSniNonDotPort_NotDetected()
    {
        var info = MakeInfo("api.example.com");

        Assert.False(DotDetector.IsLikelyDot(info, 443));
    }

    [Fact]
    public void IsLikelyDot_EmptySni_NonDotPort_NotDetected()
    {
        var info = MakeInfo(string.Empty);

        Assert.False(DotDetector.IsLikelyDot(info, 443));
    }
}
