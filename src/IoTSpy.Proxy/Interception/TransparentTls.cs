using System.Net.Security;
using System.Security.Authentication;
using IoTSpy.Core.Interfaces;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Server-side TLS handshake for connections intercepted by redirect (Gateway Redirect mode),
/// where the proxy only knows the destination IP address and not the hostname the client asked for.
/// </summary>
public static class TransparentTls
{
    private const int MaxHostNameLength = 253;

    /// <summary>
    /// Chooses the hostname to issue a certificate for and to connect upstream with: the SNI name
    /// from the ClientHello when it is a valid DNS name, otherwise <paramref name="fallbackHost"/>
    /// (the original destination IP). SNI comes from the client, so it is validated before it is
    /// used as a certificate subject or a connection target.
    /// </summary>
    public static string ResolveHostName(string? sniHostName, string fallbackHost)
    {
        if (string.IsNullOrWhiteSpace(sniHostName)) return fallbackHost;

        var name = sniHostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || name.Length > MaxHostNameLength) return fallbackHost;

        // Dns only: rejects IP literals, empty labels and characters that are not valid in a name.
        return Uri.CheckHostName(name) == UriHostNameType.Dns ? name : fallbackHost;
    }

    /// <summary>
    /// Authenticates <paramref name="ssl"/> as a server, presenting a certificate for the SNI name
    /// the client sent (falling back to <paramref name="fallbackHost"/>). Returns the hostname used,
    /// which callers should use as the upstream target so the upstream TLS handshake validates
    /// against the real name rather than an IP address.
    /// </summary>
    public static async Task<string> AuthenticateAsync(
        SslStream ssl, string fallbackHost, ICertificateAuthority ca, CancellationToken ct)
    {
        var rootCa = await ca.GetOrCreateRootCaAsync(ct);
        var host = fallbackHost;

        await ssl.AuthenticateAsServerAsync(async (_, hello, _, token) =>
        {
            host = ResolveHostName(hello.ServerName, fallbackHost);
            var leaf = await ca.GetOrCreateHostCertificateAsync(host, token);
            return new SslServerAuthenticationOptions
            {
                ServerCertificateContext = TransparentProxyServer.BuildCertContext(leaf, rootCa),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            };
        }, state: null, ct);

        return host;
    }
}
