using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Protocols.Doh;
using IoTSpy.Proxy.Resilience;
using IoTSpy.Proxy.Tls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Transparent proxy server for GatewayRedirect mode. Receives traffic redirected by
/// iptables REDIRECT rules and recovers the original destination using SO_ORIGINAL_DST.
///
/// Typical iptables setup:
///   iptables -t nat -A PREROUTING -p tcp --dport 80  -j REDIRECT --to-port 9999
///   iptables -t nat -A PREROUTING -p tcp --dport 443 -j REDIRECT --to-port 9999
/// </summary>
public class TransparentProxyServer(
    ICertificateAuthority ca,
    ICapturePublisher publisher,
    IManipulationService manipulationService,
    IOpenRtbService openRtbService,
    IAnomalyDetector anomalyDetector,
    IAnomalyAlertPublisher anomalyPublisher,
    SslStripService sslStripService,
    IServiceScopeFactory scopeFactory,
    IPerHostConnectPipelineCache perHostPipelines,
    IUpstreamConnectionPool connectionPool,
    IProtocolMessageWriter protocolMessageWriter,
    ILogger<TransparentProxyServer> logger)
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;
    private readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning => _listener is not null;
    public int ActiveConnections => _activeConnections;

    // Linux netfilter constants for SO_ORIGINAL_DST
    private const int SOL_IP = 0;
    private const int SO_ORIGINAL_DST = 80;

    public async Task StartAsync(int port, string listenAddress, CancellationToken ct = default)
    {
        if (IsRunning) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var ip = listenAddress is "0.0.0.0" or "*"
            ? IPAddress.Any
            : IPAddress.Parse(listenAddress);

        _listener = new TcpListener(ip, port);
        _listener.Start();
        logger.LogInformation("Transparent proxy listening on {Addr}:{Port}", listenAddress, port);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        logger.LogInformation("Transparent proxy stopping — waiting for {Count} active connection(s) to drain",
            _activeConnections);

        var timeout = drainTimeout ?? TimeSpan.FromSeconds(10);
        if (_activeConnections > 0)
        {
            await Task.WhenAny(_drainTcs.Task, Task.Delay(timeout));
            if (_activeConnections > 0)
                logger.LogWarning("Drain timeout reached — {Count} connection(s) still active", _activeConnections);
        }

        logger.LogInformation("Transparent proxy stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Transparent proxy accept error");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        using (client)
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 30_000;

            try
            {
                var clientIp = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown";

                // Recover the original destination from the NAT table
                if (!TryGetOriginalDestination(client.Client, out var destIp, out var destPort))
                {
                    logger.LogDebug("Could not recover original destination for client {ClientIp}", clientIp);
                    return;
                }

                var host = destIp.ToString();
                logger.LogDebug("Transparent intercept: {ClientIp} → {Host}:{Port}", clientIp, host, destPort);

                using var scope = scopeFactory.CreateScope();
                var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
                var settings = await settingsRepo.GetAsync(ct);

                // Determine if this is TLS traffic (common TLS ports: 443, 8443, 8883)
                var isTlsPort = destPort is 443 or 8443 or 8883;

                if (isTlsPort && settings.CaptureTls)
                    await HandleTlsTransparentAsync(client, host, destPort, clientIp, settings, scope, ct);
                else if (isTlsPort)
                    await HandleTlsPassthroughAsync(client, host, destPort, clientIp, scope, ct);
                else
                    await HandlePlainTransparentAsync(client, host, destPort, clientIp, settings, scope, ct);
            }
            catch (BrokenCircuitException ex)
            {
                logger.LogWarning("Circuit breaker open for upstream: {Message}", ex.Message);
                await TrySend503Async(client);
            }
            catch (TimeoutRejectedException ex)
            {
                logger.LogWarning("Upstream connection timed out: {Message}", ex.Message);
                await TrySend503Async(client);
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                logger.LogTrace("Client disconnected before TLS handshake completed");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Transparent proxy client handler error");
            }
        }
        if (Interlocked.Decrement(ref _activeConnections) == 0 && _cts?.IsCancellationRequested == true)
            _drainTcs.TrySetResult();
    }

    // ── TLS MITM for transparent traffic ─────────────────────────────────────

    private async Task HandleTlsTransparentAsync(
        TcpClient client, string host, int port,
        string clientIp, ProxySettings settings, IServiceScope scope, CancellationToken ct)
    {
        var certEntry = await ca.GetOrCreateHostCertificateAsync(host, ct);
        var rootCaEntry = await ca.GetOrCreateRootCaAsync(ct);
        var certContext = BuildCertContext(certEntry, rootCaEntry);

        var clientStream = client.GetStream();
        using var sslClient = new SslStream(clientStream, leaveInnerStreamOpen: true);
        await sslClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = certContext,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        }, ct);

        // Upstream connections come from the shared pool (reused across every client
        // device, not just this one) rather than a dedicated connection opened per client.
        await InterceptHttpStreamAsync(sslClient, host, port, isTls: true, "https",
            sslClient.SslProtocol.ToString(), sslClient.NegotiatedCipherSuite.ToString(),
            clientIp, settings, scope, ct);
    }

    // ── Plain HTTP transparent interception ──────────────────────────────────

    private async Task HandlePlainTransparentAsync(
        TcpClient client, string host, int port,
        string clientIp, ProxySettings settings, IServiceScope scope, CancellationToken ct)
    {
        var clientStream = client.GetStream();

        // Upstream connections come from the shared pool (reused across every client
        // device, not just this one) rather than a dedicated connection opened per client.
        await InterceptHttpStreamAsync(clientStream, host, port, isTls: false, "http",
            string.Empty, string.Empty, clientIp, settings, scope, ct);
    }

    // ── TLS passthrough with metadata capture ─────────────────────────────

    private async Task HandleTlsPassthroughAsync(
        TcpClient client, string host, int port,
        string clientIp, IServiceScope scope, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var metadata = new TlsMetadata();
        long clientToServer = 0;
        long serverToClient = 0;

        var clientStream = client.GetStream();

        // Buffer initial bytes from client to parse ClientHello
        var initialBuf = new byte[16 * 1024];
        var initialLen = 0;

        while (initialLen < 5)
        {
            var n = await clientStream.ReadAsync(initialBuf.AsMemory(initialLen), ct);
            if (n == 0) return;
            initialLen += n;
        }

        var neededLen = TlsClientHelloParser.GetRecordLength(initialBuf.AsSpan(0, initialLen));
        if (neededLen > 0)
        {
            if (neededLen > initialBuf.Length)
                Array.Resize(ref initialBuf, neededLen);

            while (initialLen < neededLen)
            {
                var n = await clientStream.ReadAsync(initialBuf.AsMemory(initialLen), ct);
                if (n == 0) break;
                initialLen += n;
            }
        }

        // Parse ClientHello for SNI and JA3
        var sniHost = host;
        ClientHelloInfo? clientHelloForDotCheck = null;
        if (TlsClientHelloParser.TryParse(initialBuf.AsSpan(0, initialLen), out var clientHello))
        {
            clientHelloForDotCheck = clientHello;
            if (!string.IsNullOrEmpty(clientHello.SniHostname))
                sniHost = clientHello.SniHostname;

            metadata.SniHostname = clientHello.SniHostname;
            metadata.Ja3Hash = clientHello.Ja3Hash;
            metadata.Ja3Raw = clientHello.Ja3Raw;
            metadata.ClientTlsVersion = clientHello.TlsVersion;
            metadata.ClientCipherSuites = clientHello.CipherSuites;
            metadata.ClientExtensions = clientHello.Extensions;

            logger.LogInformation(
                "TLS passthrough ClientHello: {ClientIp}→{SniHostname} JA3={Ja3Hash} DnsCorrelationKey={DnsCorrelationKey}",
                clientIp, sniHost, clientHello.Ja3Hash, $"{clientIp}→{sniHost}");
        }
        else
        {
            logger.LogDebug("TLS passthrough: could not parse ClientHello from {ClientIp} to {Host}:{Port}",
                clientIp, host, port);
        }

        // Connect upstream — per-host pipeline; use SNI hostname if available.
        var upstream = new TcpClient();
        var sniOrHost = sniHost != host ? sniHost : host;
        await perHostPipelines.GetPipeline(sniOrHost, port).ExecuteAsync(async token =>
        {
            await upstream.ConnectAsync(sniOrHost, port, token);
            return upstream;
        }, ct);

        using (upstream)
        {
            var upstreamStream = upstream.GetStream();

            // Forward buffered ClientHello
            await upstreamStream.WriteAsync(initialBuf.AsMemory(0, initialLen), ct);
            clientToServer += initialLen;

            // Read ServerHello + Certificate from upstream
            var serverBuf = new byte[32 * 1024];
            var serverBufLen = 0;
            var parsedServerHello = false;
            var parsedCert = false;
            var isTls13 = false;

            // In TLS 1.3 the Certificate message is encrypted, so we can only extract
            // ServerHello metadata — skip certificate parsing when 1.3 is negotiated.
            for (var attempt = 0; attempt < 5 && (!parsedServerHello || (!parsedCert && !isTls13)); attempt++)
            {
                var n = await upstreamStream.ReadAsync(serverBuf.AsMemory(serverBufLen), ct);
                if (n == 0) break;
                serverBufLen += n;

                await clientStream.WriteAsync(serverBuf.AsMemory(serverBufLen - n, n), ct);
                serverToClient += n;

                var parsePos = 0;
                while (parsePos < serverBufLen)
                {
                    var remaining = serverBuf.AsSpan(parsePos, serverBufLen - parsePos);
                    var recordLen = TlsServerHelloParser.GetRecordLength(remaining);
                    if (recordLen == 0 || recordLen > remaining.Length)
                        break;

                    var hsType = TlsServerHelloParser.GetHandshakeType(remaining);
                    if (hsType == 0x02 && !parsedServerHello)
                    {
                        if (TlsServerHelloParser.TryParseServerHello(remaining, out var serverHelloInfo))
                        {
                            parsedServerHello = true;
                            metadata.Ja3sHash = serverHelloInfo.Ja3sHash;
                            metadata.Ja3sRaw = serverHelloInfo.Ja3sRaw;
                            metadata.ServerTlsVersion = serverHelloInfo.TlsVersion;
                            metadata.ServerCipherSuite = serverHelloInfo.CipherSuite;
                            metadata.ServerExtensions = serverHelloInfo.Extensions;

                            isTls13 = serverHelloInfo.TlsVersion >= 0x0304;

                            logger.LogInformation(
                                "TLS passthrough ServerHello: {SniHostname} TLS={TlsVersion:X4} Cipher={CipherSuite:X4} JA3S={Ja3sHash}",
                                sniHost, serverHelloInfo.TlsVersion, serverHelloInfo.CipherSuite, serverHelloInfo.Ja3sHash);

                            if (isTls13)
                                logger.LogDebug("TLS 1.3 detected for {SniHostname} — certificate is encrypted, skipping cert extraction", sniHost);
                        }
                    }
                    else if (hsType == 0x0B && !parsedCert && !isTls13) // Certificate (TLS 1.2 only)
                    {
                        if (TlsServerHelloParser.TryParseCertificate(remaining, out var certInfo))
                        {
                            parsedCert = true;
                            metadata.CertSubject = certInfo.Subject;
                            metadata.CertIssuer = certInfo.Issuer;
                            metadata.CertSerial = certInfo.SerialNumber;
                            metadata.CertSanList = certInfo.SanList;
                            metadata.CertNotBefore = certInfo.NotBefore;
                            metadata.CertNotAfter = certInfo.NotAfter;
                            metadata.CertSha256Fingerprint = certInfo.Sha256Fingerprint;

                            logger.LogInformation(
                                "TLS passthrough certificate: {SniHostname} Subject={CertSubject} Issuer={CertIssuer} " +
                                "SHA256={CertFingerprint} SAN=[{CertSan}] Expires={CertExpiry}",
                                sniHost, certInfo.Subject, certInfo.Issuer,
                                certInfo.Sha256Fingerprint, string.Join(", ", certInfo.SanList),
                                certInfo.NotAfter);
                        }
                    }

                    parsePos += recordLen;
                }
            }

            // Relay remaining traffic bidirectionally
            await Task.WhenAll(
                RelayAndCountAsync(clientStream, upstreamStream, v => Interlocked.Add(ref clientToServer, v), ct),
                RelayAndCountAsync(upstreamStream, clientStream, v => Interlocked.Add(ref serverToClient, v), ct));

            metadata.ClientToServerBytes = Interlocked.Read(ref clientToServer);
            metadata.ServerToClientBytes = Interlocked.Read(ref serverToClient);
        }

        sw.Stop();

        // Record capture
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var captures = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();
        var device = await GetOrRegisterDeviceAsync(devices, clientIp, ct);

        // Flag likely DNS-over-TLS (port 853 or a known-resolver SNI) — encrypted payload
        // gives no further visibility, so this is the only detection signal available.
        if (clientHelloForDotCheck is not null)
            metadata.IsLikelyDot = DotDetector.IsLikelyDot(clientHelloForDotCheck, port);

        var capture = new CapturedRequest
        {
            DeviceId = device?.Id,
            Method = "CONNECT",
            Scheme = "tls-passthrough",
            Host = sniHost,
            Port = port,
            Path = "/",
            IsTls = true,
            TlsVersion = FormatTlsVersion(metadata.ServerTlsVersion),
            TlsCipherSuite = $"0x{metadata.ServerCipherSuite:X4}",
            Protocol = InterceptionProtocol.TlsPassthrough,
            Timestamp = started,
            DurationMs = sw.ElapsedMilliseconds,
            ClientIp = clientIp,
            RequestBodySize = metadata.ClientToServerBytes,
            ResponseBodySize = metadata.ServerToClientBytes,
            TlsMetadataJson = JsonSerializer.Serialize(metadata)
        };

        await captures.AddAsync(capture, ct);
        await publisher.PublishAsync(capture, ct);

        logger.LogInformation(
            "TLS passthrough complete: {ClientIp}→{SniHostname}:{Port} Duration={DurationMs}ms " +
            "C→S={ClientToServerBytes}B S→C={ServerToClientBytes}B JA3={Ja3Hash} DnsCorrelationKey={DnsCorrelationKey}",
            clientIp, sniHost, port, sw.ElapsedMilliseconds,
            metadata.ClientToServerBytes, metadata.ServerToClientBytes,
            metadata.Ja3Hash, $"{clientIp}→{sniHost}");
    }

    private static async Task RelayAndCountAsync(
        Stream source, Stream dest, Action<long> addBytes, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await source.ReadAsync(buf, ct);
                if (n == 0) break;
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                addBytes(n);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    private static string FormatTlsVersion(ushort version) => version switch
    {
        0x0301 => "TLS 1.0",
        0x0302 => "TLS 1.1",
        0x0303 => "TLS 1.2",
        0x0304 => "TLS 1.3",
        _ => $"0x{version:X4}"
    };

    // ── HTTP stream interception (shared with ExplicitProxyServer) ───────────

    private async Task InterceptHttpStreamAsync(
        Stream clientStream,
        string host, int port, bool isTls, string scheme,
        string tlsVersion, string tlsCipher,
        string clientIp, ProxySettings settings,
        IServiceScope scope, CancellationToken ct)
    {
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var captures = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();

        async Task<(string? firstLine, string headers, string body, byte[] bodyBytes, Stream upstream)> WriteAndReadWithRetryAsync(
            string wReqLine, string wReqHeaders, byte[] wReqBodyBytes)
        {
            var upstream = await connectionPool.CheckoutAsync(host, port, isTls, ct);
            await WriteHttpMessageAsync(upstream, wReqLine, wReqHeaders, wReqBodyBytes, ct);
            var result = await ReadHttpMessageAsync(upstream, settings.MaxBodySizeKb, ct, isResponse: true);
            if (result.firstLine is null)
            {
                logger.LogInformation(
                    "Stale pooled upstream connection to {Host}:{Port} detected, reconnecting and retrying {ReqLine}",
                    host, port, wReqLine);
                connectionPool.Discard(upstream);
                upstream = await connectionPool.CheckoutAsync(host, port, isTls, ct);
                await WriteHttpMessageAsync(upstream, wReqLine, wReqHeaders, wReqBodyBytes, ct);
                result = await ReadHttpMessageAsync(upstream, settings.MaxBodySizeKb, ct, isResponse: true);
            }
            return (result.firstLine, result.headers, result.body, result.bodyBytes, upstream);
        }

        while (!ct.IsCancellationRequested)
        {
            var (reqLine, reqHeaders, reqBody, reqBodyBytes) = await ReadHttpMessageAsync(clientStream, settings.MaxBodySizeKb, ct);
            if (reqLine is null) break;

            var started = DateTimeOffset.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Parse request and apply manipulation pipeline
            ParseRequestLine(reqLine, out var method, out var path, out var query);
            var httpMsg = new HttpMessage
            {
                Method = method, Host = host, Port = port, Path = path, Query = query, Scheme = scheme,
                RequestLine = reqLine, RequestHeaders = reqHeaders, RequestBody = reqBody
            };

            // OpenRTB PII stripping (runs before general manipulation rules)
            var contentType = ExtractHeaderValue(reqHeaders, "Content-Type") ?? "";
            var modified = false;
            if (openRtbService.IsOpenRtbRequest(contentType, path, reqBody))
            {
                var rtbModified = await openRtbService.ProcessAndStripAsync(httpMsg, ManipulationPhase.Request, ct);
                if (rtbModified) modified = true;
            }

            // Phase 4: Apply request-phase manipulation rules
            var manipModified = await manipulationService.ApplyAsync(httpMsg, ManipulationPhase.Request, ct);
            if (manipModified) modified = true;

            if (modified)
            {
                reqLine = httpMsg.RequestLine;
                reqHeaders = httpMsg.RequestHeaders;
                reqBody = httpMsg.RequestBody;
                reqBodyBytes = Encoding.UTF8.GetBytes(reqBody);
                reqHeaders = UpdateContentLength(reqHeaders, reqBodyBytes.Length);
            }

            string? statusLine = null; string respHeaders = string.Empty; string respBody = string.Empty; byte[] respBodyBytes = [];
            Stream? transparentUpstream = null;
            if (!string.IsNullOrEmpty(reqLine))
                (statusLine, respHeaders, respBody, respBodyBytes, transparentUpstream) = await WriteAndReadWithRetryAsync(reqLine, reqHeaders, reqBodyBytes);

            // Decode Content-Encoding for storage (original compressed bytes still forwarded to client)
            if (respBodyBytes.Length > 0)
            {
                var enc = ExtractHeaderValue(respHeaders, "Content-Encoding");
                var respMime = ExtractHeaderValue(respHeaders, "Content-Type")?.Split(';')[0].Trim() ?? "";
                byte[] storageBytes = respBodyBytes;

                if (!string.IsNullOrEmpty(enc))
                {
                    var decoded = TryDecompressBody(respBodyBytes, enc);
                    if (decoded is not null)
                    {
                        storageBytes = decoded;
                        if (!IsBinaryContentType(respMime))
                            respBody = Encoding.UTF8.GetString(decoded);
                    }
                }

                // Encode binary bodies as base64 to prevent UTF-8 corruption and
                // SQLite null-byte truncation (e.g. PNG/JPEG bytes stored as TEXT).
                if (IsBinaryContentType(respMime))
                    respBody = "b64:" + Convert.ToBase64String(storageBytes);
            }

            // SSL stripping: intercept HTTPS redirects and follow them transparently
            if (settings.SslStrip && statusLine is not null)
            {
                ParseStatusLine(statusLine, out var redirectCode, out _);
                var httpsLocation = SslStripService.GetHttpsRedirectLocation(redirectCode, respHeaders);
                if (httpsLocation is not null)
                {
                    logger.LogInformation(
                        "SSL strip: intercepting redirect {StatusCode} → {HttpsLocation} for {ClientIp}, " +
                        "fetching over HTTPS and serving as HTTP. DnsCorrelationKey={DnsCorrelationKey}",
                        redirectCode, httpsLocation, clientIp, $"{clientIp}→{host}");

                    var stripped = await sslStripService.FetchHttpsAsync(
                        httpsLocation, reqHeaders, reqBodyBytes, method, settings.MaxBodySizeKb, ct);
                    if (stripped is not null)
                    {
                        statusLine = stripped.Value.statusLine;
                        respHeaders = stripped.Value.headers;
                        respBody = stripped.Value.body;
                        respBodyBytes = stripped.Value.bodyBytes;
                        modified = true;
                    }
                }
            }

            // Apply response-phase manipulation pipeline
            if (statusLine is not null)
            {
                ParseStatusLine(statusLine, out var sc, out _);
                httpMsg.StatusLine = statusLine;
                httpMsg.StatusCode = sc;
                httpMsg.ResponseHeaders = respHeaders;
                httpMsg.ResponseBody = respBody;

                // OpenRTB PII stripping on response
                var respContentType = ExtractHeaderValue(respHeaders, "Content-Type") ?? "";
                if (openRtbService.IsOpenRtbRequest(respContentType, path, respBody))
                {
                    var rtbRespModified = await openRtbService.ProcessAndStripAsync(httpMsg, ManipulationPhase.Response, ct);
                    if (rtbRespModified) modified = true;
                }

                // Phase 4: Apply response-phase manipulation rules
                var respModified = await manipulationService.ApplyAsync(httpMsg, ManipulationPhase.Response, ct);
                if (respModified)
                {
                    modified = true;
                    statusLine = httpMsg.StatusLine;
                    respHeaders = httpMsg.ResponseHeaders;
                    respBody = httpMsg.ResponseBody;
                    respBodyBytes = Encoding.UTF8.GetBytes(respBody);
                    respHeaders = UpdateContentLength(respHeaders, respBodyBytes.Length);
                }
            }

            // SSL strip: always remove HSTS and rewrite https links in non-redirect responses
            if (settings.SslStrip && statusLine is not null)
            {
                respHeaders = SslStripService.StripResponseHeaders(respHeaders);
            }

            sw.Stop();

            if (statusLine is not null)
                await WriteHttpMessageAsync(clientStream, statusLine, respHeaders, respBodyBytes, ct);

            ParseRequestLine(reqLine ?? "", out method, out path, out query);
            ParseStatusLine(statusLine, out var statusCode, out var statusMsg);

            var device = await GetOrRegisterDeviceAsync(devices, clientIp, ct);
            var capture = new CapturedRequest
            {
                DeviceId = device?.Id,
                Method = method,
                Scheme = scheme,
                Host = host,
                Port = port,
                Path = path,
                Query = query,
                RequestHeaders = reqHeaders,
                RequestBody = settings.CaptureRequestBodies ? TruncateForStorage(reqBody, settings.MaxBodySizeKb) : string.Empty,
                RequestBodySize = reqBodyBytes.Length,
                StatusCode = statusCode,
                StatusMessage = statusMsg,
                ResponseHeaders = respHeaders,
                ResponseBody = settings.CaptureResponseBodies ? TruncateForStorage(respBody, settings.MaxBodySizeKb) : string.Empty,
                ResponseBodySize = respBodyBytes.Length,
                IsTls = scheme == "https",
                TlsVersion = tlsVersion,
                TlsCipherSuite = tlsCipher,
                Protocol = scheme == "https" ? InterceptionProtocol.Https : InterceptionProtocol.Http,
                Timestamp = started,
                DurationMs = sw.ElapsedMilliseconds,
                ClientIp = clientIp,
                IsModified = modified
            };

            // Detect gRPC traffic by content-type
            if (contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
                capture.Protocol = InterceptionProtocol.Grpc;

            // Detect DNS-over-HTTPS (RFC 8484) and decode the embedded query for reporting
            var dohResult = DohDetector.TryDetect(method, $"{path}{query}", reqHeaders, reqBody);
            if (DohDetector.TryBuildPersistedMessage(dohResult, capture.DeviceId, capture.Timestamp) is { } dohMessage)
                protocolMessageWriter.TryEnqueue(dohMessage);

            await captures.AddAsync(capture, ct);
            await publisher.PublishAsync(capture, ct);

            // WebSocket upgrade — switch to frame relay mode
            if (statusCode == 101 &&
                respHeaders.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                capture.Protocol = scheme == "https"
                    ? InterceptionProtocol.WebSocketTls
                    : InterceptionProtocol.WebSocket;
                await captures.UpdateAsync(capture, ct);

                logger.LogInformation("WebSocket upgrade detected for {Host}{Path}, relaying frames", host, path);
                // An upgraded connection is never reusable for ordinary requests again —
                // discard it from the pool once the relay finishes, rather than returning it.
                await RelayWebSocketFramesAsync(clientStream, transparentUpstream!, capture.Id,
                    host, clientIp, captures, ct);
                connectionPool.Discard(transparentUpstream!);
                break;
            }

            // Phase 8.5: Feed the anomaly detector and publish any triggered alerts
            if (statusCode > 0)
            {
                var alerts = anomalyDetector.Record(host, capture.DurationMs, capture.ResponseBodySize, statusCode);
                foreach (var alert in alerts)
                {
                    logger.LogDebug("Anomaly detected on {Host}: {Type} (deviation={Factor:F1}σ)",
                        alert.Host, alert.AlertType, alert.DeviationFactor);
                    await anomalyPublisher.PublishAsync(alert, ct);
                }
            }

            if (transparentUpstream is not null)
            {
                var transparentReusable = IsConnectionReusable(statusLine, respHeaders);
                if (transparentReusable) connectionPool.Return(host, port, isTls, transparentUpstream);
                else connectionPool.Discard(transparentUpstream);
            }

            if (respHeaders.Contains("Connection: close", StringComparison.OrdinalIgnoreCase) ||
                (reqLine ?? "").EndsWith("HTTP/1.0"))
                break;
        }
    }

    // ── WebSocket frame relay ─────────────────────────────────────────────

    private async Task RelayWebSocketFramesAsync(
        Stream clientStream, Stream upstreamStream, Guid captureId,
        string host, string clientIp, ICaptureRepository captures, CancellationToken ct)
    {
        var sequence = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task RelayDirection(Stream source, Stream dest, bool isFromClient, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var headerRead = await ReadAtLeastAsync(source, buffer, 2, token);
                    if (headerRead < 2) break;

                    var byte0 = buffer[0];
                    var byte1 = buffer[1];
                    var opcode = (WebSocketOpcode)(byte0 & 0x0F);
                    var masked = (byte1 & 0x80) != 0;
                    long payloadLen = byte1 & 0x7F;
                    var headerSize = 2;

                    if (payloadLen == 126)
                    {
                        headerRead = await ReadAtLeastAsync(source, buffer.AsMemory(headerRead), 4 - headerRead, token);
                        payloadLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
                        headerSize = 4;
                    }
                    else if (payloadLen == 127)
                    {
                        headerRead = await ReadAtLeastAsync(source, buffer.AsMemory(headerRead), 10 - headerRead, token);
                        payloadLen = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(2));
                        headerSize = 10;
                    }

                    if (masked) headerSize += 4;

                    if (headerRead < headerSize)
                    {
                        var need = headerSize - headerRead;
                        await source.ReadExactlyAsync(buffer.AsMemory(headerRead, need), token);
                    }

                    await dest.WriteAsync(buffer.AsMemory(0, headerSize), token);

                    byte[]? maskKey = masked ? buffer.AsSpan(headerSize - 4, 4).ToArray() : null;
                    var payloadBytes = new List<byte>();
                    long remaining = payloadLen;
                    int maskOffset = 0;

                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(remaining, buffer.Length);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, chunk), token);
                        await dest.WriteAsync(buffer.AsMemory(0, chunk), token);

                        if (payloadBytes.Count < 64 * 1024)
                        {
                            var captureLen = (int)Math.Min(chunk, 64 * 1024 - payloadBytes.Count);
                            var slice = buffer.AsSpan(0, captureLen).ToArray();
                            if (maskKey != null)
                                for (var i = 0; i < slice.Length; i++)
                                    slice[i] ^= maskKey[(maskOffset + i) % 4];
                            payloadBytes.AddRange(slice);
                        }
                        maskOffset += chunk;
                        remaining -= chunk;
                    }

                    var seq = Interlocked.Increment(ref sequence);
                    string? text = null;
                    if (opcode is WebSocketOpcode.Text or WebSocketOpcode.Close)
                    {
                        try { text = Encoding.UTF8.GetString(payloadBytes.ToArray()); }
                        catch { /* binary */ }
                    }

                    var frame = new WebSocketFrame
                    {
                        CaptureId = captureId,
                        Fin = (byte0 & 0x80) != 0,
                        Opcode = opcode,
                        Masked = masked,
                        PayloadLength = payloadLen,
                        PayloadText = text,
                        PayloadBinary = opcode == WebSocketOpcode.Binary ? payloadBytes.ToArray() : null,
                        IsFromClient = isFromClient,
                        SequenceNumber = seq
                    };

                    await publisher.PublishWebSocketFrameAsync(frame, ct);

                    if (opcode == WebSocketOpcode.Close) break;
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogTrace(ex, "WebSocket relay ended ({Dir})", isFromClient ? "client→upstream" : "upstream→client");
            }
            finally
            {
                await cts.CancelAsync();
            }
        }

        await Task.WhenAll(
            RelayDirection(clientStream, upstreamStream, isFromClient: true, cts.Token),
            RelayDirection(upstreamStream, clientStream, isFromClient: false, cts.Token));
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, int minBytes, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < minBytes)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    // ── SO_ORIGINAL_DST (Linux netfilter) ────────────────────────────────────

    /// <summary>
    /// Recovers the original destination address from a connection redirected by
    /// iptables REDIRECT. Only works on Linux with netfilter.
    /// </summary>
    private static bool TryGetOriginalDestination(Socket socket, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = 0;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            // struct sockaddr_in = 16 bytes: family(2) + port(2) + addr(4) + zero(8)
            var optval = new byte[16];
            socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)SO_ORIGINAL_DST, optval);

            // Port is bytes 2-3 in network byte order
            port = (optval[2] << 8) | optval[3];
            // IP is bytes 4-7
            ip = new IPAddress(new ReadOnlySpan<byte>(optval, 4, 4));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Helpers (shared HTTP parsing logic) ──────────────────────────────────

    private static async Task TrySend503Async(TcpClient client)
    {
        try
        {
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await client.GetStream().WriteAsync(response);
        }
        catch { /* best-effort */ }
    }

    private static async Task<Device?> GetOrRegisterDeviceAsync(
        IDeviceRepository devices, string ip, CancellationToken ct)
    {
        if (ip is "unknown" or "127.0.0.1" or "::1") return null;
        return await devices.UpsertByIpAsync(new Device { IpAddress = ip }, ct);
    }

    // Hard ceiling on bytes read for a single message body, independent of the
    // user-configurable MaxBodySizeKb (which only caps what gets persisted for review).
    // Purely a memory-safety backstop against a pathological Content-Length.
    private const int WireBodySafetyCeilingBytes = 200 * 1024 * 1024;

    private static async Task<(string? firstLine, string headers, string body, byte[] bodyBytes)> ReadHttpMessageAsync(
        Stream stream, int maxBodyKb, CancellationToken ct, bool isResponse = false)
    {
        string? firstLine = null;
        var headerLines = new List<string>();
        int contentLength = 0;
        bool chunked = false;

        while (true)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null) return (null, "", "", []);
            if (firstLine is null) { firstLine = line; continue; }
            if (string.IsNullOrEmpty(line)) break;
            headerLines.Add(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);
            if (line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                chunked = true;
        }

        var headers = string.Join("\r\n", headerLines);
        byte[] bodyBytes = [];
        var body = string.Empty;

        // Per RFC 7230 §3.3.3 rule 1: a response with a 1xx, 204, or 304 status code is
        // always terminated by the header block alone and can never have a body, no matter
        // what Content-Length/Transfer-Encoding headers say. These respond over a
        // keep-alive connection the server has no intention of closing, so treating them
        // as close-delimited (or trusting a stray Content-Length) can hang forever waiting
        // for bytes that will never arrive.
        var noBody = isResponse && firstLine is not null && IsBodylessStatus(firstLine);

        if (noBody)
        {
            // No body to read — leave bodyBytes/body empty regardless of what the
            // (possibly stray/incorrect) headers claim.
        }
        else if (contentLength > 0)
        {
            var readLen = Math.Min(contentLength, WireBodySafetyCeilingBytes);
            bodyBytes = new byte[readLen];
            await stream.ReadExactlyAsync(bodyBytes, ct);
            body = Encoding.UTF8.GetString(bodyBytes);
            if (contentLength > WireBodySafetyCeilingBytes)
            {
                var drain = new byte[contentLength - WireBodySafetyCeilingBytes];
                await stream.ReadExactlyAsync(drain, ct);
            }
        }
        else if (chunked)
        {
            (bodyBytes, body) = await ReadChunkedBodyAsync(stream, WireBodySafetyCeilingBytes, ct);
        }

        var closeDelimited = false;
        if (isResponse && !noBody && contentLength <= 0 && !chunked)
        {
            // Neither Content-Length nor chunked encoding: per RFC 7230 §3.3.3 this is only
            // legal for a response, whose body is then delimited by the connection closing.
            // Common for simple/embedded local HTTP(S) servers (IoT devices, HTTP/1.0-style
            // servers). Read until EOF — the connection is done after this message either way.
            (bodyBytes, body) = await ReadUntilCloseAsync(stream, WireBodySafetyCeilingBytes, ct);
            closeDelimited = true;
        }

        // Normalize headers: replace Transfer-Encoding and Content-Length to match actual body bytes
        if (chunked || closeDelimited || (contentLength > 0 && bodyBytes.Length != contentLength))
        {
            var fixedLines = headerLines
                .Where(l => !l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                         && !l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                         && !(closeDelimited && l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (closeDelimited)
                fixedLines.Add("Connection: close");
            if (bodyBytes.Length > 0)
                fixedLines.Add($"Content-Length: {bodyBytes.Length}");
            headers = string.Join("\r\n", fixedLines);
        }

        return (firstLine, headers, body, bodyBytes);
    }

    private static async Task<(byte[] bytes, string text)> ReadUntilCloseAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var rawList = new List<byte>();
        var buf = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            if (rawList.Count < maxBytes)
            {
                var take = Math.Min(read, maxBytes - rawList.Count);
                rawList.AddRange(buf.AsSpan(0, take));
            }
        }
        var bytes = rawList.ToArray();
        return (bytes, Encoding.UTF8.GetString(bytes));
    }

    private static async Task<(byte[] bytes, string text)> ReadChunkedBodyAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var rawList = new List<byte>();
        var sb = new StringBuilder();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct);
            if (sizeLine is null) break;
            var chunkSize = Convert.ToInt32(sizeLine.Trim().Split(';')[0], 16);
            if (chunkSize == 0)
            {
                await ReadLineAsync(stream, ct);
                break;
            }
            var buf = new byte[chunkSize];
            await stream.ReadExactlyAsync(buf, ct);
            await ReadLineAsync(stream, ct);
            if (rawList.Count < maxBytes)
            {
                var take = Math.Min(chunkSize, maxBytes - rawList.Count);
                rawList.AddRange(buf.AsSpan(0, take));
                sb.Append(Encoding.UTF8.GetString(buf, 0, take));
            }
        }
        return (rawList.ToArray(), sb.ToString());
    }

    private static async Task WriteHttpMessageAsync(
        Stream stream, string firstLine, string headers, byte[] bodyBytes, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append(firstLine).Append("\r\n");
        if (!string.IsNullOrEmpty(headers)) head.Append(headers).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, ct);
    }

    private static string UpdateContentLength(string headers, int byteCount)
    {
        var lines = headers.Split("\r\n")
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byteCount > 0)
            lines.Add($"Content-Length: {byteCount}");
        return string.Join("\r\n", lines);
    }

    // Caps only what gets persisted to the capture DB for review — never applied to bytes
    // actually forwarded on the wire (see WireBodySafetyCeilingBytes).
    private static string TruncateForStorage(string body, int maxBodyKb)
    {
        var maxChars = maxBodyKb * 1024;
        return body.Length > maxChars ? body[..maxChars] : body;
    }

    private static bool IsBodylessStatus(string statusLine)
    {
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var code)) return false;
        return code is 204 or 304 || (code >= 100 && code < 200);
    }

    /// <summary>
    /// Whether an upstream connection can be returned to the pool for reuse: HTTP/1.1 is
    /// persistent by default unless "Connection: close" is present; HTTP/1.0 is the reverse
    /// — non-persistent by default unless the server explicitly opts in with
    /// "Connection: keep-alive". Missing this distinction meant HTTP/1.0 responses (which
    /// the server had already closed) got treated as reusable, defeating pooling entirely.
    /// </summary>
    private static bool IsConnectionReusable(string? statusLine, string headers)
    {
        if (headers.Contains("Connection: close", StringComparison.OrdinalIgnoreCase)) return false;
        if (statusLine is not null && statusLine.StartsWith("HTTP/1.0", StringComparison.OrdinalIgnoreCase))
            return headers.Contains("Connection: keep-alive", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(256);
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            if (buf[0] == '\n') return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(buf[0]);
        }
    }

    private static void ParseRequestLine(string line, out string method, out string path, out string query)
    {
        var parts = line.Split(' ');
        method = parts.Length > 0 ? parts[0] : "GET";
        var rawPath = parts.Length > 1 ? parts[1] : "/";
        var qi = rawPath.IndexOf('?');
        path = qi < 0 ? rawPath : rawPath[..qi];
        query = qi < 0 ? string.Empty : rawPath[(qi + 1)..];
    }

    private static void ParseStatusLine(string? line, out int code, out string message)
    {
        if (line is null) { code = 0; message = string.Empty; return; }
        var parts = line.Split(' ', 3);
        code = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 0;
        message = parts.Length > 2 ? parts[2] : string.Empty;
    }

    private static string? ExtractHeaderValue(string headers, string headerName)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                return line[(headerName.Length + 1)..].Trim();
        }
        return null;
    }

    private static bool IsBinaryContentType(string mimeType) =>
        (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !mimeType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/zip", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/gzip", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/x-tar", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "application/x-bzip2", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "font/woff", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "font/woff2", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "font/ttf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "font/otf", StringComparison.OrdinalIgnoreCase);

    private static byte[]? TryDecompressBody(byte[] compressed, string encoding)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var output = new MemoryStream();
            Stream decompressor = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => null!
            };
            if (decompressor is null) return null;
            using (decompressor)
                decompressor.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static SslStreamCertificateContext BuildCertContext(CertificateEntry leaf, CertificateEntry rootCa)
    {
        using var ephemeral = X509Certificate2.CreateFromPem(leaf.CertificatePem, leaf.PrivateKeyPem);
        // No 'using' — SslStreamCertificateContext owns leafCert's lifetime.
        var leafCert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        var caCert = X509CertificateLoader.LoadCertificate(PemToBytes(rootCa.CertificatePem));
        return SslStreamCertificateContext.Create(leafCert, new X509Certificate2Collection(caCert), offline: true);
    }

    private static byte[] PemToBytes(string pem)
    {
        var b64 = pem
            .Replace("-----BEGIN CERTIFICATE-----", "")
            .Replace("-----END CERTIFICATE-----", "")
            .Replace("\n", "").Replace("\r", "").Trim();
        return Convert.FromBase64String(b64);
    }
}
