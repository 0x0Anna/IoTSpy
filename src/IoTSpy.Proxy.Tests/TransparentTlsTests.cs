using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Interception;
using IoTSpy.Proxy.Tls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Proxy.Tests;

public class TransparentTlsTests
{
    private const string Fallback = "203.0.113.9";

    [Theory]
    [InlineData("www.google.com", "www.google.com")]
    [InlineData("WWW.Google.COM", "www.google.com")]
    [InlineData("api.example.com.", "api.example.com")]
    [InlineData("  device.local.test ", "device.local.test")]
    public void ResolveHostName_ValidSni_IsUsed(string sni, string expected)
        => Assert.Equal(expected, TransparentTls.ResolveHostName(sni, Fallback));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.0.2.10")]                 // IP literal is not a name
    [InlineData("evil.com, O=Hacked")]           // would corrupt the certificate subject
    [InlineData("a b.example.com")]
    [InlineData("bad..example.com")]
    [InlineData("-bad.example.com")]
    public void ResolveHostName_MissingOrInvalidSni_FallsBackToDestination(string? sni)
        => Assert.Equal(Fallback, TransparentTls.ResolveHostName(sni, Fallback));

    [Fact]
    public void ResolveHostName_OverlongSni_FallsBack()
    {
        var name = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".com"; // > 253 chars
        Assert.Equal(Fallback, TransparentTls.ResolveHostName(name, Fallback));
    }

    [Theory]
    [InlineData("203.0.113.7", "device.example.test", "device.example.test")]
    [InlineData("203.0.113.7", "example.test:80", "example.test")]
    [InlineData("203.0.113.7", "Example.TEST", "example.test")]
    [InlineData("203.0.113.7", null, "203.0.113.7")]
    [InlineData("203.0.113.7", "", "203.0.113.7")]
    [InlineData("203.0.113.7", "192.0.2.5", "203.0.113.7")]            // IP literal, not a name
    [InlineData("203.0.113.7", "evil.com, O=x", "203.0.113.7")]       // invalid name
    [InlineData("203.0.113.7", "[::1]:80", "203.0.113.7")]
    [InlineData("www.google.com", "other.example.com", "www.google.com")] // already a name: keep it
    public void ResolveCaptureHost_UsesHostHeaderOnlyWhenDestinationIsAnIp(string connectHost, string? header, string expected)
        => Assert.Equal(expected, TransparentTls.ResolveCaptureHost(connectHost, header));

    [Fact]
    public async Task Authenticate_ClientSendsSni_PresentsCertificateValidForThatName()
    {
        var ca = BuildCa();
        var root = await ca.GetOrCreateRootCaAsync(TestContext.Current.CancellationToken);

        var (host, nameMismatch, chainOk) = await HandshakeAsync(ca, root, clientTargetHost: "www.example.test");

        Assert.Equal("www.example.test", host);
        Assert.False(nameMismatch, "leaf certificate must be valid for the SNI name the client asked for");
        Assert.True(chainOk, "leaf must chain to the IoTSpy root CA");
    }

    [Fact]
    public async Task Authenticate_ClientSendsNoSni_FallsBackToDestinationIp()
    {
        var ca = BuildCa();
        var root = await ca.GetOrCreateRootCaAsync(TestContext.Current.CancellationToken);

        // .NET does not send SNI when the target host is an IP address, like many IoT devices.
        var (host, _, chainOk) = await HandshakeAsync(ca, root, clientTargetHost: "127.0.0.1", serverFallback: "127.0.0.1");

        Assert.Equal("127.0.0.1", host);
        Assert.True(chainOk);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<(string Host, bool NameMismatch, bool ChainOk)> HandshakeAsync(
        CertificateAuthority ca, CertificateEntry root, string clientTargetHost, string serverFallback = Fallback)
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var nameMismatch = false;
            var chainOk = false;
            var rootCert = X509CertificateLoader.LoadCertificate(PemToBytes(root.CertificatePem));

            var clientTask = Task.Run(async () =>
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, errors) =>
                {
                    nameMismatch = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(rootCert);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    chainOk = cert is not null && chain.Build(new X509Certificate2(cert));
                    return true; // record the outcome; assertions are made by the test
                });
                await ssl.AuthenticateAsClientAsync(clientTargetHost);
            }, ct);

            using var accepted = await listener.AcceptTcpClientAsync(ct);
            using var serverSsl = new SslStream(accepted.GetStream(), false);
            var host = await TransparentTls.AuthenticateAsync(serverSsl, serverFallback, ca, ct);
            await clientTask;
            return (host, nameMismatch, chainOk);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A real CertificateAuthority over an in-memory repository mock.</summary>
    private static CertificateAuthority BuildCa()
    {
        var store = new Dictionary<string, CertificateEntry>();
        CertificateEntry? savedRoot = null;

        var repo = Substitute.For<ICertificateRepository>();
        repo.GetRootCaAsync(Arg.Any<CancellationToken>()).Returns(_ => savedRoot);
        repo.GetByHostnameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => store.GetValueOrDefault(c.Arg<string>()));
        repo.SaveAsync(Arg.Any<CertificateEntry>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var e = c.Arg<CertificateEntry>();
                if (e.IsRootCa) savedRoot = e; else store[e.CommonName] = e;
                return e;
            });

        var settings = Substitute.For<IProxySettingsRepository>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new ProxySettings());

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ICertificateRepository)).Returns(repo);
        sp.GetService(typeof(IProxySettingsRepository)).Returns(settings);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(sp);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return new CertificateAuthority(scopeFactory, NullLogger<CertificateAuthority>.Instance);
    }

    private static byte[] PemToBytes(string pem) => Convert.FromBase64String(pem
        .Replace("-----BEGIN CERTIFICATE-----", "").Replace("-----END CERTIFICATE-----", "")
        .Replace("\n", "").Replace("\r", "").Trim());
}
