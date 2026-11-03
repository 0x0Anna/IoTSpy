using System.Net.Security;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Interception;
using IoTSpy.Proxy.Tls;
using Xunit;

namespace IoTSpy.Proxy.Tests;

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public class TlsInterceptionPolicyTests
{
    private const string Client = "192.0.2.111";

    private static ClientHelloInfo Hello(params string[] alpn) => new() { SniHostname = "x", AlpnProtocols = [.. alpn] };

    // ── ALPN ─────────────────────────────────────────────────────────────────

    [Fact]
    public void H2OnlyClient_IsPassedThrough()
    {
        var verdict = new TlsInterceptionPolicy(TimeProvider.System)
            .Decide(Hello("h2"), "firestore.googleapis.com", Client, new ProxySettings());

        Assert.True(verdict.Passthrough);
        Assert.Contains("h2", verdict.Reason);
    }

    [Theory]
    [InlineData("h2", "http/1.1")]
    [InlineData("http/1.1")]
    [InlineData]
    public void ClientOfferingHttp1OrNoAlpn_IsIntercepted(params string[] alpn)
    {
        var verdict = new TlsInterceptionPolicy(TimeProvider.System)
            .Decide(Hello(alpn), "www.bing.com", Client, new ProxySettings());

        Assert.False(verdict.Passthrough);
    }

    [Fact]
    public void CustomAlpn_WithoutHttp1_IsPassedThrough()
    {
        // e.g. AWS IoT MQTT over 443
        var verdict = new TlsInterceptionPolicy(TimeProvider.System)
            .Decide(Hello("x-amzn-mqtt-ca"), "a1.iot.us-east-1.amazonaws.com", Client, new ProxySettings());

        Assert.True(verdict.Passthrough);
    }

    [Fact]
    public void UnparseableHello_IsIntercepted()
    {
        var verdict = new TlsInterceptionPolicy(TimeProvider.System).Decide(null, "1.2.3.4", Client, new ProxySettings());

        Assert.False(verdict.Passthrough);
    }

    // ── Non-TLS detection ────────────────────────────────────────────────────

    [Theory]
    [InlineData("160301", true)]               // TLS 1.0 record version (normal ClientHello)
    [InlineData("160303", true)]
    [InlineData("a1b2c3d4e5f6", false)]         // proprietary binary protocol on 443
    [InlineData("474554202f", false)]           // "GET /" — plain HTTP on a TLS port
    [InlineData("16", false)]                   // too short to tell
    [InlineData("", false)]
    public void LooksLikeTlsHandshake(string hex, bool expected) =>
        Assert.Equal(expected, TransparentProxyServer.LooksLikeTlsHandshake(Convert.FromHexString(hex)));

    // ── Bypass list ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*.apple.com", "itunes.apple.com", true)]
    [InlineData("*.apple.com", "apple.com", true)]
    [InlineData("*.apple.com", "notapple.com", false)]
    [InlineData("*.apple.com", "apple.com.evil.net", false)]
    [InlineData("gateway.icloud.com", "GATEWAY.icloud.com.", true)]
    [InlineData("gateway.icloud.com", "x.gateway.icloud.com", false)]
    [InlineData("a.com\n*.b.com, c.com", "c.com", true)]
    [InlineData("a.com\r\n  *.b.com", "deep.x.b.com", true)]
    [InlineData("", "a.com", false)]
    public void MatchesHostList(string patterns, string host, bool expected) =>
        Assert.Equal(expected, TlsInterceptionPolicy.MatchesHostList(patterns, host));

    [Fact]
    public void HostOnBypassList_IsPassedThrough()
    {
        var settings = new ProxySettings { TlsPassthroughHosts = "*.apple.com" };

        var verdict = new TlsInterceptionPolicy(TimeProvider.System).Decide(Hello("h2", "http/1.1"), "xp.apple.com", Client, settings);

        Assert.True(verdict.Passthrough);
        Assert.Contains("passthrough list", verdict.Reason);
    }

    // ── Handshake-failure tracking ───────────────────────────────────────────

    [Fact]
    public void RepeatedFailures_AutoBypass_OnlyWhenEnabled()
    {
        var time = new FakeTimeProvider();
        var policy = new TlsInterceptionPolicy(time);
        for (var i = 0; i < TlsInterceptionPolicy.FailureThreshold; i++)
            policy.RecordHandshakeFailure(Client, "pinned.example.com");

        Assert.False(policy.Decide(Hello(), "pinned.example.com", Client, new ProxySettings()).Passthrough);

        var verdict = policy.Decide(Hello(), "pinned.example.com", Client, new ProxySettings { AutoPassthroughOnTlsFailure = true });
        Assert.True(verdict.Passthrough);
        Assert.StartsWith("auto:", verdict.Reason);

        // Scoped to the client+host pair
        Assert.False(policy.Decide(Hello(), "pinned.example.com", "192.0.2.204", new ProxySettings { AutoPassthroughOnTlsFailure = true }).Passthrough);
    }

    [Fact]
    public void AutoBypass_ExpiresAfterDuration()
    {
        var time = new FakeTimeProvider();
        var policy = new TlsInterceptionPolicy(time);
        for (var i = 0; i < TlsInterceptionPolicy.FailureThreshold; i++)
            policy.RecordHandshakeFailure(Client, "pinned.example.com");

        time.Advance(TlsInterceptionPolicy.AutoBypassDuration + TimeSpan.FromSeconds(1));

        Assert.False(policy.IsAutoBypassed(Client, "pinned.example.com"));
    }

    [Fact]
    public void FailuresSpreadBeyondWindow_DoNotTriggerBypass()
    {
        var time = new FakeTimeProvider();
        var policy = new TlsInterceptionPolicy(time);
        for (var i = 0; i < TlsInterceptionPolicy.FailureThreshold; i++)
        {
            policy.RecordHandshakeFailure(Client, "flaky.example.com");
            time.Advance(TlsInterceptionPolicy.FailureWindow + TimeSpan.FromSeconds(1));
        }

        Assert.False(policy.IsAutoBypassed(Client, "flaky.example.com"));
    }

    [Fact]
    public void SuccessfulHandshake_ClearsFailures()
    {
        var policy = new TlsInterceptionPolicy(new FakeTimeProvider());
        policy.RecordHandshakeFailure(Client, "h.example.com");
        policy.RecordHandshakeFailure(Client, "h.example.com");
        policy.RecordHandshakeSuccess(Client, "h.example.com");
        policy.RecordHandshakeFailure(Client, "h.example.com");

        Assert.False(policy.IsAutoBypassed(Client, "h.example.com"));
    }

    [Fact]
    public void FailureRecording_IsRateLimitedPerPair()
    {
        var time = new FakeTimeProvider();
        var policy = new TlsInterceptionPolicy(time);

        Assert.True(policy.RecordHandshakeFailure(Client, "telemetry.example.com"));
        Assert.False(policy.RecordHandshakeFailure(Client, "telemetry.example.com"));
        Assert.False(policy.RecordHandshakeFailure(Client, "telemetry.example.com"));
        Assert.True(policy.RecordHandshakeFailure(Client, "other.example.com"));

        time.Advance(TlsInterceptionPolicy.FailureRecordInterval);
        Assert.True(policy.RecordHandshakeFailure(Client, "telemetry.example.com"));
    }
}

/// <summary>ALPN extraction, checked against real ClientHellos produced by .NET's SslStream client.</summary>
public class ClientHelloAlpnTests
{
    private static async Task<byte[]> CaptureClientHelloAsync(params SslApplicationProtocol[] alpn)
    {
        var capture = new CapturingStream();
        using var ssl = new SslStream(capture, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        var options = new SslClientAuthenticationOptions { TargetHost = "firestore.googleapis.com" };
        if (alpn.Length > 0) options.ApplicationProtocols = [.. alpn];
        try { await ssl.AuthenticateAsClientAsync(options, TestContext.Current.CancellationToken); }
        catch { /* no server: the handshake fails after the ClientHello is written */ }
        return capture.Written.ToArray();
    }

    [Fact]
    public async Task ExtractsH2Only()
    {
        var bytes = await CaptureClientHelloAsync(SslApplicationProtocol.Http2);

        Assert.True(TlsClientHelloParser.TryParse(bytes, out var info));
        Assert.Equal(["h2"], info.AlpnProtocols);
        Assert.Equal("firestore.googleapis.com", info.SniHostname);
    }

    [Fact]
    public async Task ExtractsMultipleInOrder()
    {
        var bytes = await CaptureClientHelloAsync(SslApplicationProtocol.Http2, SslApplicationProtocol.Http11);

        Assert.True(TlsClientHelloParser.TryParse(bytes, out var info));
        Assert.Equal(["h2", "http/1.1"], info.AlpnProtocols);
    }

    [Fact]
    public async Task NoAlpnExtension_YieldsEmptyList()
    {
        var bytes = await CaptureClientHelloAsync();

        Assert.True(TlsClientHelloParser.TryParse(bytes, out var info));
        Assert.Empty(info.AlpnProtocols);
    }

    /// <summary>Records writes; reads report EOF so the handshake stops after the ClientHello.</summary>
    private sealed class CapturingStream : Stream
    {
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}

public class PrefixedStreamTests
{
    [Fact]
    public async Task ReadsPrefixThenInner_AcrossSmallReads()
    {
        var inner = new MemoryStream("world"u8.ToArray());
        var stream = new PrefixedStream("hello "u8.ToArray(), inner);

        var result = new MemoryStream();
        var buf = new byte[4];
        int n;
        while ((n = await stream.ReadAsync(buf, TestContext.Current.CancellationToken)) > 0)
            result.Write(buf, 0, n);

        Assert.Equal("hello world", System.Text.Encoding.ASCII.GetString(result.ToArray()));
    }

    [Fact]
    public async Task WritesGoToInner()
    {
        var inner = new MemoryStream();
        var stream = new PrefixedStream("ignored"u8.ToArray(), inner);

        await stream.WriteAsync("abc"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal("abc", System.Text.Encoding.ASCII.GetString(inner.ToArray()));
    }

    [Fact]
    public async Task ZeroByteRead_WithPrefixPending_CompletesWithoutTouchingInner()
    {
        var inner = new BlockingStream();
        var stream = new PrefixedStream("x"u8.ToArray(), inner);

        var n = await stream.ReadAsync(Memory<byte>.Empty, TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(0, n);
        Assert.False(inner.ReadCalled);
    }

    /// <summary>
    /// End-to-end over real sockets: the server peeks the ClientHello off the socket, then completes a
    /// TLS handshake by replaying it through PrefixedStream — exactly what the transparent proxy does.
    /// Regression for a hang where SslStream's zero-byte read was forwarded to the idle socket.
    /// </summary>
    [Fact]
    public async Task SslHandshake_OverPeekedClientHello_Completes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cert = CreateSelfSigned("prefixed.test");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            var net = tcp.GetStream();
            var head = new byte[16 * 1024];
            var len = 0;
            while (len < 5) len += await net.ReadAsync(head.AsMemory(len), ct);
            var need = TlsClientHelloParser.GetRecordLength(head.AsSpan(0, len));
            while (len < need) len += await net.ReadAsync(head.AsMemory(len), ct);

            using var ssl = new SslStream(new PrefixedStream(head.AsMemory(0, len), net), leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, ct);
            var buf = new byte[5];
            var got = 0;
            while (got < 5) got += await ssl.ReadAsync(buf.AsMemory(got), ct);
            await ssl.WriteAsync(buf, ct);
        }, ct);

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
        using var clientSsl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "prefixed.test",
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
        }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        await clientSsl.WriteAsync("hello"u8.ToArray(), ct);
        var echo = new byte[5];
        var read = 0;
        while (read < 5) read += await clientSsl.ReadAsync(echo.AsMemory(read), ct);

        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(echo));
        Assert.Equal(SslApplicationProtocol.Http11, clientSsl.NegotiatedApplicationProtocol);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10), ct);
        listener.Stop();
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateSelfSigned(string name)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={name}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(cert.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
    }

    private sealed class BlockingStream : Stream
    {
        public bool ReadCalled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalled = true; Thread.Sleep(Timeout.Infinite); return 0; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ReadCalled = true; await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
