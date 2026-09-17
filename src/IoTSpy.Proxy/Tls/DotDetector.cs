namespace IoTSpy.Proxy.Tls;

/// <summary>
/// Heuristically flags DNS-over-TLS (RFC 7858) connections. DoT is indistinguishable from
/// other TLS traffic at the byte level — it is plain TLS carrying DNS messages instead of
/// HTTP — so detection relies on the two external signals available at the ClientHello:
/// the IANA-assigned DoT port (853) and the SNI hostname matching a known public DoT/DoH
/// resolver. This is a heuristic allowlist, not an exhaustive resolver database.
/// </summary>
public static class DotDetector
{
    /// <summary>IANA-assigned port for DNS-over-TLS.</summary>
    public const int DotPort = 853;

    private static readonly HashSet<string> KnownResolverHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dns.google",
        "cloudflare-dns.com",
        "one.one.one.one",
        "1dot1dot1dot1.cloudflare-dns.com",
        "dns.quad9.net",
        "family.cloudflare-dns.com",
        "dns.nextdns.io",
        "dns.adguard.com",
    };

    /// <summary>
    /// Returns true when the connection is likely DNS-over-TLS: either it targets the
    /// well-known DoT port (853), or the ClientHello's SNI matches a known public
    /// DoT/DoH resolver hostname.
    /// </summary>
    public static bool IsLikelyDot(ClientHelloInfo info, int destinationPort)
    {
        if (destinationPort == DotPort) return true;

        var sni = info.SniHostname;
        return !string.IsNullOrEmpty(sni) && KnownResolverHostnames.Contains(sni);
    }
}
