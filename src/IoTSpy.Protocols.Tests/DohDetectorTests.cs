using IoTSpy.Protocols.Doh;
using Xunit;

namespace IoTSpy.Protocols.Tests;

public class DohDetectorTests
{
    /// <summary>
    /// Builds a minimal, valid DNS wire-format query for "example.com" type A.
    /// </summary>
    private static byte[] BuildDnsQuery()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Header
        bw.Write((byte)0x12); bw.Write((byte)0x34); // Transaction ID
        bw.Write((byte)0x01); bw.Write((byte)0x00); // Flags: standard query, recursion desired
        bw.Write((byte)0x00); bw.Write((byte)0x01); // QDCOUNT = 1
        bw.Write((byte)0x00); bw.Write((byte)0x00); // ANCOUNT
        bw.Write((byte)0x00); bw.Write((byte)0x00); // NSCOUNT
        bw.Write((byte)0x00); bw.Write((byte)0x00); // ARCOUNT

        // Question: example.com A IN
        foreach (var label in new[] { "example", "com" })
        {
            bw.Write((byte)label.Length);
            bw.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bw.Write((byte)0x00); // root label
        bw.Write((byte)0x00); bw.Write((byte)0x01); // QTYPE = A
        bw.Write((byte)0x00); bw.Write((byte)0x01); // QCLASS = IN

        return ms.ToArray();
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public void TryDetect_GetWithDnsQueryParam_DetectsAndDecodes()
    {
        var dnsBytes = BuildDnsQuery();
        var encoded = ToBase64Url(dnsBytes);

        var result = DohDetector.TryDetect("GET", $"/dns-query?dns={encoded}", headersRaw: null, body: (byte[]?)null);

        Assert.True(result.IsDoh);
        Assert.NotNull(result.Query);
        Assert.Single(result.Query!.Questions);
        Assert.Equal("example.com", result.Query!.Questions[0].Name);
    }

    [Fact]
    public void TryDetect_PostWithDnsMessageContentType_DetectsAndDecodes()
    {
        var dnsBytes = BuildDnsQuery();
        const string headers = "Content-Type: application/dns-message\r\nHost: dns.google\r\n";

        var result = DohDetector.TryDetect("POST", "/dns-query", headers, dnsBytes);

        Assert.True(result.IsDoh);
        Assert.NotNull(result.Query);
        Assert.Equal("example.com", result.Query!.Questions[0].Name);
    }

    [Fact]
    public void TryDetect_NormalHttpRequest_NotDetected()
    {
        const string headers = "Content-Type: application/json\r\nHost: api.example.com\r\n";

        var result = DohDetector.TryDetect("GET", "/api/v1/things", headers, body: (byte[]?)null);

        Assert.False(result.IsDoh);
        Assert.Null(result.Query);
    }

    [Fact]
    public void TryDetect_MalformedBase64_DetectedAsDohButDoesNotDecode()
    {
        var result = DohDetector.TryDetect("GET", "/dns-query?dns=not-valid-base64!!!", headersRaw: null, body: (byte[]?)null);

        Assert.True(result.IsDoh);
        Assert.Null(result.Query);
    }

    [Fact]
    public void TryDetect_MalformedDnsBytesInValidBase64_DetectedAsDohButDoesNotDecode()
    {
        var junk = new byte[] { 0x01, 0x02, 0x03 }; // too short to be a DNS header
        var encoded = ToBase64Url(junk);

        var result = DohDetector.TryDetect("GET", $"/dns-query?dns={encoded}", headersRaw: null, body: (byte[]?)null);

        Assert.True(result.IsDoh);
        Assert.Null(result.Query);
    }

    [Fact]
    public void TryDetect_AcceptHeaderDnsMessage_Detected()
    {
        const string headers = "Accept: application/dns-message\r\n";

        var result = DohDetector.TryDetect("POST", "/resolve", headers, body: (byte[]?)null);

        Assert.True(result.IsDoh);
    }

    [Fact]
    public void TryDetect_NeverThrows_OnNullPath()
    {
        var result = DohDetector.TryDetect("GET", null, null, (byte[]?)null);

        Assert.False(result.IsDoh);
    }
}
