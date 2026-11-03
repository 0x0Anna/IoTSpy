using IoTSpy.Core.Enums;

namespace IoTSpy.Core.Models;

public class ProxySettings
{
    public int Id { get; set; } = 1;
    public int ProxyPort { get; set; } = 8888;
    public ProxyMode Mode { get; set; } = ProxyMode.ExplicitProxy;
    public bool IsRunning { get; set; }
    public bool CaptureTls { get; set; } = true;
    public bool CaptureRequestBodies { get; set; } = true;
    public bool CaptureResponseBodies { get; set; } = true;
    public int MaxBodySizeKb { get; set; } = 1024;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public string PasswordHash { get; set; } = string.Empty;

    // ── GatewayRedirect mode ─────────────────────────────────────────────
    public int TransparentProxyPort { get; set; } = 9999;

    // ── ArpSpoof mode ────────────────────────────────────────────────────
    public string TargetDeviceIp { get; set; } = string.Empty;
    public string GatewayIp { get; set; } = string.Empty;
    public string NetworkInterface { get; set; } = string.Empty;

    // ── CA certificate customization ────────────────────────────────────
    public string CaCommonName { get; set; } = "IoTSpy CA";
    public string CaOrganization { get; set; } = "IoTSpy";
    public string CaCountry { get; set; } = "US";
    public int CaValidityYears { get; set; } = 10;

    // ── TLS interception exceptions ──────────────────────────────────────
    /// <summary>Hosts whose TLS is relayed untouched instead of intercepted — typically
    /// certificate-pinned services. One pattern per line (commas also accepted): an exact host
    /// (<c>api.example.com</c>) or a wildcard suffix (<c>*.apple.com</c>, which also matches
    /// <c>apple.com</c>). Connections are still recorded as passthrough captures.</summary>
    public string TlsPassthroughHosts { get; set; } = string.Empty;

    /// <summary>When a client repeatedly rejects the interception certificate for a host
    /// (pinning, or a device that doesn't trust the IoTSpy CA), relay that client+host untouched
    /// for a while instead of breaking it. Off by default: it lets traffic through that is
    /// currently failing, which can matter when a device is under investigation.</summary>
    public bool AutoPassthroughOnTlsFailure { get; set; }

    // ── SSL stripping ────────────────────────────────────────────────────
    public bool SslStrip { get; set; }

    // ── Startup behaviour ────────────────────────────────────────────────
    /// <summary>Automatically start the proxy when the server process starts.</summary>
    public bool AutoStart { get; set; }
}
