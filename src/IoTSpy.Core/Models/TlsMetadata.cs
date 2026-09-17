namespace IoTSpy.Core.Models;

/// <summary>
/// Metadata extracted from a TLS handshake during passthrough (non-MITM) interception.
/// Stored as JSON in <see cref="CapturedRequest.TlsMetadataJson"/>.
/// </summary>
public class TlsMetadata
{
    // ClientHello-derived
    public string SniHostname { get; set; } = string.Empty;
    public string Ja3Hash { get; set; } = string.Empty;
    public string Ja3Raw { get; set; } = string.Empty;
    public ushort ClientTlsVersion { get; set; }
    public List<ushort> ClientCipherSuites { get; set; } = [];
    public List<ushort> ClientExtensions { get; set; } = [];

    // ServerHello-derived
    public string Ja3sHash { get; set; } = string.Empty;
    public string Ja3sRaw { get; set; } = string.Empty;
    public ushort ServerTlsVersion { get; set; }
    public ushort ServerCipherSuite { get; set; }
    public List<ushort> ServerExtensions { get; set; } = [];

    // Server certificate info
    public string CertSubject { get; set; } = string.Empty;
    public string CertIssuer { get; set; } = string.Empty;
    public string CertSerial { get; set; } = string.Empty;
    public List<string> CertSanList { get; set; } = [];
    public DateTimeOffset? CertNotBefore { get; set; }
    public DateTimeOffset? CertNotAfter { get; set; }
    public string CertSha256Fingerprint { get; set; } = string.Empty;

    // Traffic stats
    public long ClientToServerBytes { get; set; }
    public long ServerToClientBytes { get; set; }

    /// <summary>Heuristically flagged as DNS-over-TLS (port 853 or a known-resolver SNI) by
    /// <c>IoTSpy.Proxy.Tls.DotDetector</c>. See that type for the detection rationale.</summary>
    public bool IsLikelyDot { get; set; }
}
