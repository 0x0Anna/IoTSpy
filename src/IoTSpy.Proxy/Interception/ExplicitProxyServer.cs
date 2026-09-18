using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Protocols.Doh;
using IoTSpy.Proxy.Passive;
using IoTSpy.Proxy.Resilience;
using IoTSpy.Proxy.Tls;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Timeout;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// HTTP/HTTPS explicit proxy server. Listens for HTTP CONNECT tunnels and plain HTTP
/// requests, performs TLS MITM when CaptureTls is enabled, and publishes every
/// captured transaction to the <see cref="ICapturePublisher"/>.
/// </summary>
public class ExplicitProxyServer(
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
    IPassiveProxyBuffer passiveBuffer,
    ICaptureBatchWriter captureBatchWriter,
    IProtocolMessageWriter protocolMessageWriter,
    ILogger<ExplicitProxyServer> logger)
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;
    private TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning => _listener is not null;
    public int ActiveConnections => _activeConnections;

    public async Task StartAsync(int port, string listenAddress, CancellationToken ct = default)
    {
        if (IsRunning) return;
        _drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ip = listenAddress is "0.0.0.0" or "*"
            ? IPAddress.Any
            : IPAddress.Parse(listenAddress);
        _listener = new TcpListener(ip, port);
        _listener.Start();
        logger.LogInformation("Explicit proxy listening on {Addr}:{Port}", listenAddress, port);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        logger.LogInformation("Explicit proxy stopping — waiting for {Count} active connection(s) to drain",
            _activeConnections);

        // Wait for active connections to finish, up to the drain timeout
        var timeout = drainTimeout ?? TimeSpan.FromSeconds(10);
        var drainTask = _drainTcs.Task;
        if (_activeConnections > 0)
        {
            await Task.WhenAny(drainTask, Task.Delay(timeout));
            if (_activeConnections > 0)
                logger.LogWarning("Drain timeout reached — {Count} connection(s) still active", _activeConnections);
        }

        logger.LogInformation("Explicit proxy stopped");
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
                logger.LogWarning(ex, "Accept error");
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
                var stream = client.GetStream();
                var clientIp = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown";
                var requestLine = await ReadLineAsync(stream, ct);
                if (string.IsNullOrEmpty(requestLine)) return;

                using var scope = scopeFactory.CreateScope();
                var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
                var settings = await settingsRepo.GetAsync(ct);

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                    await HandleConnectAsync(stream, requestLine, clientIp, settings, scope, ct);
                else
                    await HandlePlainHttpAsync(stream, requestLine, clientIp, settings, scope, ct);
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
                // Expected: client closed connection mid-handshake (connectivity probe, untrusted CA, etc.)
                logger.LogTrace("Client disconnected before TLS handshake completed");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Client handler error");
            }
        }
        if (Interlocked.Decrement(ref _activeConnections) == 0 && _cts?.IsCancellationRequested == true)
            _drainTcs.TrySetResult();
    }

    // ── HTTPS (CONNECT tunnel + TLS MITM) ──────────────────────────────────

    private async Task HandleConnectAsync(
        NetworkStream clientStream, string requestLine,
        string clientIp, ProxySettings settings, IServiceScope scope, CancellationToken ct)
    {
        // CONNECT example.com:443 HTTP/1.1
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;
        var hostPort = parts[1];
        var host = hostPort.Contains(':') ? hostPort[..hostPort.LastIndexOf(':')] : hostPort;
        int.TryParse(hostPort.Contains(':') ? hostPort[(hostPort.LastIndexOf(':') + 1)..] : "443", out var port);

        // Drain remaining CONNECT headers
        while (true)
        {
            var line = await ReadLineAsync(clientStream, ct);
            if (string.IsNullOrEmpty(line)) break;
        }

        // Respond 200 Connection established
        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
        await clientStream.WriteAsync(ok, ct);

        if (!settings.CaptureTls)
        {
            await HandleTlsPassthroughAsync(clientStream, host, port, clientIp, scope, ct);
            return;
        }

        // TLS MITM
        var certEntry = await ca.GetOrCreateHostCertificateAsync(host, ct);
        var rootCaEntry = await ca.GetOrCreateRootCaAsync(ct);
        var certContext = BuildCertContext(certEntry, rootCaEntry);

        using var sslClient = new SslStream(clientStream, leaveInnerStreamOpen: true);
        await sslClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            // Use ServerCertificateContext instead of ServerCertificate so that the CA
            // cert is included in the TLS Certificate message. Without this, SChannel
            // only looks in the Windows certificate store for chain certs — our CA is
            // stored in SQLite, not the store, so iOS receives only the leaf cert and
            // may fail to build the trust chain even with the CA installed on device.
            ServerCertificateContext = certContext,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        }, ct);

        // Upstream connections come from the shared pool (reused across every client
        // device, not just this one) rather than a dedicated connection opened per
        // client — see UpstreamConnectionPool for why.
        await InterceptHttpStreamAsync(sslClient, host, port, isTls: true, "https",
            sslClient.SslProtocol.ToString(), sslClient.NegotiatedCipherSuite.ToString(),
            clientIp, settings, scope, ct);
    }

    // ── Plain HTTP ──────────────────────────────────────────────────────────

    private async Task HandlePlainHttpAsync(
        NetworkStream clientStream, string requestLine,
        string clientIp, ProxySettings settings, IServiceScope scope, CancellationToken ct)
    {
        // Parse the full first request up front (headers + body, if any) using the same
        // reader InterceptHttpStreamAsync uses internally, seeded with the request line
        // already consumed by HandleClientAsync. Previously this method hand-rolled a
        // headers-only read, wrote it straight to upstream, then handed off to
        // InterceptHttpStreamAsync — whose loop unconditionally starts by reading a *new*
        // request from the client. Since a plain-HTTP client sends exactly one request and
        // then waits for the response, that second read blocked forever: every plain
        // http:// request through the proxy hung until the client's own timeout.
        var (reqLine, reqHeaders, reqBody, reqBodyBytes) =
            await ReadHttpMessageAsync(clientStream, settings.MaxBodySizeKb, ct, requestLine);
        if (reqLine is null) return;

        var hostHeader = ExtractHeaderValue(reqHeaders, "Host");
        if (hostHeader is null) return;
        var host = hostHeader.Contains(':') ? hostHeader[..hostHeader.LastIndexOf(':')] : hostHeader;
        int.TryParse(hostHeader.Contains(':') ? hostHeader[(hostHeader.LastIndexOf(':') + 1)..] : "80", out var port);
        if (port == 0) port = 80;

        // Upstream connections come from the shared pool (reused across every client
        // device, not just this one) rather than a dedicated connection opened per client.
        await InterceptHttpStreamAsync(clientStream, host, port, isTls: false, "http",
            string.Empty, string.Empty, clientIp, settings, scope, ct,
            initialRequest: (reqLine, reqHeaders, reqBody, reqBodyBytes));
    }

    // ── HTTP parse + record ─────────────────────────────────────────────────

    private async Task InterceptHttpStreamAsync(
        Stream clientStream,
        string host, int port, bool isTls, string scheme,
        string tlsVersion, string tlsCipher,
        string clientIp, ProxySettings settings,
        IServiceScope scope,
        CancellationToken ct,
        (string reqLine, string reqHeaders, string reqBody, byte[] reqBodyBytes)? initialRequest = null)
    {
        var isPassive = settings.Mode == Core.Enums.ProxyMode.Passive;
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var captures = isPassive ? null : scope.ServiceProvider.GetRequiredService<ICaptureRepository>();

        // Each request checks out a connection from the shared pool rather than reusing one
        // dedicated connection for this client's entire session — see UpstreamConnectionPool.
        // A pooled connection can be dead (the peer closed it after its own, often shorter,
        // keep-alive timeout elapsed) despite passing the pool's liveness check; detect that
        // by an immediately-empty read after writing, discard it, and retry once on a fresh
        // checkout before giving up.
        async Task<(string? firstLine, string headers, string body, byte[] bodyBytes, Stream upstream)> WriteAndReadWithRetryAsync(
            string wReqLine, string wReqHeaders, byte[] wReqBodyBytes)
        {
            var upstream = await connectionPool.CheckoutAsync(host, port, isTls, ct);
            try
            {
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
            catch
            {
                // A write/read failure other than the handled "stale connection" case (e.g. a
                // genuine network error, timeout, or cancellation) must not leak the checked-out
                // connection — the caller never gets a chance to return/discard it otherwise.
                connectionPool.Discard(upstream);
                throw;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            // Read request from client — except on the first iteration of the plain-HTTP
            // path, where HandlePlainHttpAsync already read (and passed in) the one request
            // a plain-HTTP client sends before waiting on the response.
            string? reqLine; string reqHeaders; string reqBody; byte[] reqBodyBytes;
            if (initialRequest is { } seed)
            {
                (reqLine, reqHeaders, reqBody, reqBodyBytes) = seed;
                initialRequest = null;
            }
            else
            {
                (reqLine, reqHeaders, reqBody, reqBodyBytes) = await ReadHttpMessageAsync(clientStream, settings.MaxBodySizeKb, ct);
            }
            if (reqLine is null) break;

            var started = DateTimeOffset.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            ParseRequestLine(reqLine, out var method, out var path, out var query);
            var contentType = ExtractHeaderValue(reqHeaders, "Content-Type") ?? "";

            if (isPassive)
            {
                // Passive fast-path: forward traffic unchanged, no manipulation, no DB inserts
                var (statusLine, respHeaders, respBody, respBodyBytes, passiveUpstream) =
                    await WriteAndReadWithRetryAsync(reqLine, reqHeaders, reqBodyBytes);
                var passiveReusable = statusLine is not null && IsConnectionReusable(statusLine, respHeaders);
                if (passiveReusable) connectionPool.Return(host, port, isTls, passiveUpstream);
                else connectionPool.Discard(passiveUpstream);

                if (statusLine is not null)
                    await WriteHttpMessageAsync(clientStream, statusLine, respHeaders, respBodyBytes, ct);

                ParseStatusLine(statusLine, out var sc, out var sm);
                sw.Stop();

                var passiveCapture = new CapturedRequest
                {
                    Method = method, Scheme = scheme, Host = host, Port = port,
                    Path = path, Query = query, RequestHeaders = reqHeaders,
                    RequestBody = settings.CaptureRequestBodies ? TruncateForStorage(reqBody, settings.MaxBodySizeKb) : string.Empty,
                    RequestBodySize = reqBodyBytes.Length,
                    StatusCode = sc, StatusMessage = sm,
                    ResponseHeaders = respHeaders ?? string.Empty,
                    ResponseBody = settings.CaptureResponseBodies ? TruncateForStorage(respBody ?? string.Empty, settings.MaxBodySizeKb) : string.Empty,
                    ResponseBodySize = respBodyBytes?.Length ?? 0,
                    IsTls = scheme == "https", TlsVersion = tlsVersion, TlsCipherSuite = tlsCipher,
                    Protocol = scheme == "https" ? InterceptionProtocol.Https : InterceptionProtocol.Http,
                    Timestamp = started, DurationMs = sw.ElapsedMilliseconds, ClientIp = clientIp
                };
                if (contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
                    passiveCapture.Protocol = InterceptionProtocol.Grpc;

                passiveBuffer.Add(passiveCapture);
                await publisher.PublishAsync(passiveCapture, ct);

                if (respHeaders is not null &&
                    (respHeaders.Contains("Connection: close", StringComparison.OrdinalIgnoreCase) ||
                     (reqLine).EndsWith("HTTP/1.0")))
                    break;
                continue;
            }

            // ── Active interception path ─────────────────────────────────────

            var httpMsg = new HttpMessage
            {
                Method = method, Host = host, Port = port, Path = path, Query = query, Scheme = scheme,
                RequestLine = reqLine, RequestHeaders = reqHeaders, RequestBody = reqBody
            };

            // OpenRTB PII stripping (runs before general manipulation rules)
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

            // Forward to upstream and read the response (skip if Drop action cleared the request line)
            string? statusLine2 = null; string respHeaders2 = string.Empty; string respBody2 = string.Empty; byte[] respBodyBytes2 = [];
            Stream? activeUpstream = null;
            var activeUpstreamReleased = false;
            try
            {
            if (!string.IsNullOrEmpty(reqLine))
                (statusLine2, respHeaders2, respBody2, respBodyBytes2, activeUpstream) = await WriteAndReadWithRetryAsync(reqLine, reqHeaders, reqBodyBytes);

            // Decode Content-Encoding for storage (the original compressed bytes are still
            // forwarded to the client below — we only change what gets persisted in the DB)
            if (respBodyBytes2.Length > 0)
            {
                var enc = ExtractHeaderValue(respHeaders2, "Content-Encoding");
                var respMime = ExtractHeaderValue(respHeaders2, "Content-Type")?.Split(';')[0].Trim() ?? "";
                byte[] storageBytes = respBodyBytes2;

                if (!string.IsNullOrEmpty(enc))
                {
                    var decoded = TryDecompressBody(respBodyBytes2, enc);
                    if (decoded is not null)
                    {
                        storageBytes = decoded;
                        if (!IsBinaryContentType(respMime))
                            respBody2 = Encoding.UTF8.GetString(decoded);
                    }
                }

                // Encode binary bodies as base64 to prevent UTF-8 corruption and
                // SQLite null-byte truncation (e.g. PNG/JPEG bytes stored as TEXT).
                if (IsBinaryContentType(respMime))
                    respBody2 = "b64:" + Convert.ToBase64String(storageBytes);
            }

            // SSL stripping: intercept HTTPS redirects and follow them transparently
            if (settings.SslStrip && statusLine2 is not null)
            {
                ParseStatusLine(statusLine2, out var redirectCode, out _);
                var httpsLocation = SslStripService.GetHttpsRedirectLocation(redirectCode, respHeaders2);
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
                        statusLine2 = stripped.Value.statusLine;
                        respHeaders2 = stripped.Value.headers;
                        respBody2 = stripped.Value.body;
                        respBodyBytes2 = stripped.Value.bodyBytes;
                        modified = true;
                    }
                }
            }

            // Apply response-phase manipulation pipeline
            if (statusLine2 is not null)
            {
                ParseStatusLine(statusLine2, out var sc, out _);
                httpMsg.StatusLine = statusLine2;
                httpMsg.StatusCode = sc;
                httpMsg.ResponseHeaders = respHeaders2;
                httpMsg.ResponseBody = respBody2;

                // OpenRTB PII stripping on response
                var respContentType = ExtractHeaderValue(respHeaders2, "Content-Type") ?? "";
                if (openRtbService.IsOpenRtbRequest(respContentType, path, respBody2))
                {
                    var rtbRespModified = await openRtbService.ProcessAndStripAsync(httpMsg, ManipulationPhase.Response, ct);
                    if (rtbRespModified) modified = true;
                }

                // Phase 4: Apply response-phase manipulation rules
                var respModified = await manipulationService.ApplyAsync(httpMsg, ManipulationPhase.Response, ct);
                if (respModified)
                {
                    modified = true;
                    statusLine2 = httpMsg.StatusLine;
                    respHeaders2 = httpMsg.ResponseHeaders;

                    if (httpMsg.ResponseBodySource is null)
                    {
                        respBody2 = httpMsg.ResponseBody;
                        respBodyBytes2 = Encoding.UTF8.GetBytes(respBody2);
                        respHeaders2 = UpdateContentLength(respHeaders2, respBodyBytes2.Length);
                    }
                    else
                    {
                        // Binary / streaming / range path: headers synthesised from the body source.
                        var src = httpMsg.ResponseBodySource;
                        statusLine2 = $"HTTP/1.1 {src.StatusCode} {ReasonPhrase(src.StatusCode)}";
                        respHeaders2 = ApplyBodySourceHeaders(respHeaders2, src);
                        respBody2 = $"[binary replacement: {src.ContentLength?.ToString() ?? "stream"}]";
                        respBodyBytes2 = []; // capture record only; wire write handled below
                    }
                }
            }

            // SSL strip: always remove HSTS and rewrite https links in non-redirect responses
            if (settings.SslStrip && statusLine2 is not null && httpMsg.ResponseBodySource is null)
            {
                respHeaders2 = SslStripService.StripResponseHeaders(respHeaders2);
            }

            sw.Stop();

            // Forward response to client
            if (statusLine2 is not null)
            {
                if (httpMsg.ResponseBodySource is { } bodySrc)
                {
                    await WriteHttpHeadAsync(clientStream, statusLine2, respHeaders2, ct);
                    await bodySrc.WriteToAsync(clientStream, ct);
                }
                else
                {
                    await WriteHttpMessageAsync(clientStream, statusLine2, respHeaders2, respBodyBytes2, ct);
                }
            }

            // Parse and record
            ParseRequestLine(reqLine ?? "", out method, out path, out query);
            ParseStatusLine(statusLine2, out var statusCode, out var statusMsg);

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
                ResponseHeaders = respHeaders2,
                ResponseBody = settings.CaptureResponseBodies ? TruncateForStorage(respBody2, settings.MaxBodySizeKb) : string.Empty,
                ResponseBodySize = respBodyBytes2.Length,
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

            captureBatchWriter.TryEnqueue(capture);

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

            // WebSocket upgrade — switch to frame relay mode
            if (statusCode == 101 &&
                respHeaders2.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                capture.Protocol = scheme == "https"
                    ? InterceptionProtocol.WebSocketTls
                    : InterceptionProtocol.WebSocket;
                await captures!.UpdateAsync(capture, ct);

                logger.LogInformation("WebSocket upgrade detected for {Host}{Path}, relaying frames", host, path);
                // An upgraded connection is never reusable for ordinary requests again —
                // discard it from the pool once the relay finishes, rather than returning it.
                await RelayWebSocketFramesAsync(clientStream, activeUpstream!, capture.Id,
                    host, clientIp, captures!, ct);
                connectionPool.Discard(activeUpstream!);
                activeUpstreamReleased = true;
                break;
            }

            if (activeUpstream is not null)
            {
                var activeReusable = IsConnectionReusable(statusLine2, respHeaders2);
                if (activeReusable) connectionPool.Return(host, port, isTls, activeUpstream);
                else connectionPool.Discard(activeUpstream);
                activeUpstreamReleased = true;
            }

            // HTTP/1.0 or Connection: close — stop after one exchange
            if (respHeaders2.Contains("Connection: close", StringComparison.OrdinalIgnoreCase) ||
                (reqLine ?? "").EndsWith("HTTP/1.0"))
                break;
            }
            finally
            {
                if (activeUpstream is not null && !activeUpstreamReleased)
                    connectionPool.Discard(activeUpstream);
            }
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
                    // Read WebSocket frame header (2 bytes minimum)
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

                    // Read remaining header bytes if needed
                    if (headerRead < headerSize)
                    {
                        var need = headerSize - headerRead;
                        await source.ReadExactlyAsync(buffer.AsMemory(headerRead, need), token);
                        headerRead = headerSize;
                    }

                    // Forward the header
                    await dest.WriteAsync(buffer.AsMemory(0, headerSize), token);

                    // Read and forward payload in chunks
                    byte[]? maskKey = masked ? buffer.AsSpan(headerSize - 4, 4).ToArray() : null;
                    var payloadBytes = new List<byte>();
                    long remaining = payloadLen;
                    int maskOffset = 0;

                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(remaining, buffer.Length);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, chunk), token);
                        await dest.WriteAsync(buffer.AsMemory(0, chunk), token);

                        // Capture unmasked payload (up to 64KB for storage)
                        if (payloadBytes.Count < 64 * 1024)
                        {
                            var captureLen = (int)Math.Min(chunk, 64 * 1024 - payloadBytes.Count);
                            var slice = buffer.AsSpan(0, captureLen).ToArray();
                            if (maskKey != null)
                            {
                                for (var i = 0; i < slice.Length; i++)
                                    slice[i] ^= maskKey[(maskOffset + i) % 4];
                            }
                            payloadBytes.AddRange(slice);
                        }
                        maskOffset += chunk;
                        remaining -= chunk;
                    }

                    // Publish frame capture
                    var seq = Interlocked.Increment(ref sequence);
                    string? text = null;
                    if (opcode is WebSocketOpcode.Text or WebSocketOpcode.Close)
                    {
                        try { text = Encoding.UTF8.GetString(payloadBytes.ToArray()); }
                        catch { /* binary data */ }
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
                    logger.LogTrace("WS frame {Opcode} seq={Seq} len={Len} {Dir} on {Host}",
                        opcode, seq, payloadLen, isFromClient ? "→" : "←", host);

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

    // ── TLS passthrough with metadata capture ─────────────────────────────

    private async Task HandleTlsPassthroughAsync(
        NetworkStream clientStream, string connectHost, int port,
        string clientIp, IServiceScope scope, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var metadata = new TlsMetadata();
        long clientToServer = 0;
        long serverToClient = 0;

        // Buffer initial bytes from client to parse ClientHello
        var initialBuf = new byte[16 * 1024];
        var initialLen = 0;

        // Read enough for the TLS record header
        while (initialLen < 5)
        {
            var n = await clientStream.ReadAsync(initialBuf.AsMemory(initialLen), ct);
            if (n == 0) return;
            initialLen += n;
        }

        // Determine full ClientHello record length and read it all
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
        var sniHost = connectHost;
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
                clientIp, connectHost, port);
        }

        // Connect upstream — per-host pipeline.
        var upstream = new TcpClient();
        await perHostPipelines.GetPipeline(sniHost, port).ExecuteAsync(async token =>
        {
            await upstream.ConnectAsync(sniHost, port, token);
            return upstream;
        }, ct);

        using (upstream)
        {
            var upstreamStream = upstream.GetStream();

            // Forward the buffered ClientHello to upstream
            await upstreamStream.WriteAsync(initialBuf.AsMemory(0, initialLen), ct);
            clientToServer += initialLen;

            // Read ServerHello + Certificate from upstream, parse and forward
            var serverBuf = new byte[32 * 1024];
            var serverBufLen = 0;
            var parsedServerHello = false;
            var parsedCert = false;
            var isTls13 = false;

            // Read a few TLS records from the server to get ServerHello + Certificate.
            // In TLS 1.3 the Certificate message is encrypted, so we can only extract
            // ServerHello metadata — skip certificate parsing when 1.3 is negotiated.
            for (var attempt = 0; attempt < 5 && (!parsedServerHello || (!parsedCert && !isTls13)); attempt++)
            {
                var n = await upstreamStream.ReadAsync(serverBuf.AsMemory(serverBufLen), ct);
                if (n == 0) break;
                serverBufLen += n;

                // Forward immediately to client
                await clientStream.WriteAsync(serverBuf.AsMemory(serverBufLen - n, n), ct);
                serverToClient += n;

                // Try to parse what we have so far
                var parsePos = 0;
                while (parsePos < serverBufLen)
                {
                    var remaining = serverBuf.AsSpan(parsePos, serverBufLen - parsePos);
                    var recordLen = TlsServerHelloParser.GetRecordLength(remaining);
                    if (recordLen == 0 || recordLen > remaining.Length)
                        break;

                    var hsType = TlsServerHelloParser.GetHandshakeType(remaining);
                    if (hsType == 0x02 && !parsedServerHello) // ServerHello
                    {
                        if (TlsServerHelloParser.TryParseServerHello(remaining, out var serverHello))
                        {
                            parsedServerHello = true;
                            metadata.Ja3sHash = serverHello.Ja3sHash;
                            metadata.Ja3sRaw = serverHello.Ja3sRaw;
                            metadata.ServerTlsVersion = serverHello.TlsVersion;
                            metadata.ServerCipherSuite = serverHello.CipherSuite;
                            metadata.ServerExtensions = serverHello.Extensions;

                            // TLS 1.3 (0x0304) encrypts Certificate — cannot parse it in passthrough
                            isTls13 = serverHello.TlsVersion >= 0x0304;

                            logger.LogInformation(
                                "TLS passthrough ServerHello: {SniHostname} TLS={TlsVersion:X4} Cipher={CipherSuite:X4} JA3S={Ja3sHash}",
                                sniHost, serverHello.TlsVersion, serverHello.CipherSuite, serverHello.Ja3sHash);

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

            // Relay remaining traffic bidirectionally, counting bytes
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

        captureBatchWriter.TryEnqueue(capture);

        logger.LogInformation(
            "TLS passthrough complete: {ClientIp}→{SniHostname}:{Port} Duration={DurationMs}ms " +
            "C→S={ClientToServerBytes}B S→C={ServerToClientBytes}B JA3={Ja3Hash} DnsCorrelationKey={DnsCorrelationKey}",
            clientIp, sniHost, port, sw.ElapsedMilliseconds,
            metadata.ClientToServerBytes, metadata.ServerToClientBytes,
            metadata.Ja3Hash, $"{clientIp}→{sniHost}");
    }

    private static async Task RelayAndCountAsync(
        Stream source, Stream dest,
        Action<long> addBytes, CancellationToken ct)
    {
        var localBuf = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await source.ReadAsync(localBuf, ct);
                if (n == 0) break;
                await dest.WriteAsync(localBuf.AsMemory(0, n), ct);
                addBytes(n);
            }
        }
        catch (IOException) { /* connection closed */ }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static string FormatTlsVersion(ushort version) => version switch
    {
        0x0301 => "TLS 1.0",
        0x0302 => "TLS 1.1",
        0x0303 => "TLS 1.2",
        0x0304 => "TLS 1.3",
        _ => $"0x{version:X4}"
    };

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task TrySend503Async(TcpClient client)
    {
        try
        {
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Content-Length: 0\r\n" +
                "Connection: close\r\n\r\n");
            await client.GetStream().WriteAsync(response);
        }
        catch { /* best-effort */ }
    }

    private static async Task<Device?> GetOrRegisterDeviceAsync(
        IDeviceRepository devices, string ip, CancellationToken ct)
    {
        if (ip is "unknown" or "127.0.0.1" or "::1") return null;
        var device = new Device { IpAddress = ip };
        return await devices.UpsertByIpAsync(device, ct);
    }

    // Hard ceiling on bytes read for a single message body, independent of the
    // user-configurable MaxBodySizeKb (which only caps what gets persisted for review).
    // Purely a memory-safety backstop against a pathological Content-Length.
    private const int WireBodySafetyCeilingBytes = 200 * 1024 * 1024;

    private static async Task<(string? firstLine, string headers, string body, byte[] bodyBytes)> ReadHttpMessageAsync(
        Stream stream, int maxBodyKb, CancellationToken ct, string? knownFirstLine = null, bool isResponse = false)
    {
        string? firstLine = knownFirstLine;
        var headerLines = new List<string>();
        int contentLength = 0;
        bool chunked = false;

        // Read headers
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

        // maxBodyKb (a small, user-configurable "how much to persist for review" setting,
        // 1024 KB by default) must NOT cap what's actually read off the wire — doing so
        // silently truncated any response over the cap (JS bundles, images, etc.) before
        // forwarding it to the client, corrupting it. WireBodySafetyCeilingBytes is a much
        // larger hard ceiling purely to bound memory use against a pathological/malicious
        // Content-Length; the DB-storage cap is applied separately, only to what gets
        // persisted, at the call sites that build CapturedRequest.
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
            // Drain remainder only in the pathological case that exceeds the safety ceiling
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
                await ReadLineAsync(stream, ct); // trailing CRLF
                break;
            }
            var buf = new byte[chunkSize];
            await stream.ReadExactlyAsync(buf, ct);
            await ReadLineAsync(stream, ct); // CRLF after chunk
            if (rawList.Count < maxBytes)
            {
                var take = Math.Min(chunkSize, maxBytes - rawList.Count);
                rawList.AddRange(buf.AsSpan(0, take));
                sb.Append(Encoding.UTF8.GetString(buf, 0, take));
            }
        }
        return (rawList.ToArray(), sb.ToString());
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

    private static async Task WriteHttpMessageAsync(
        Stream stream, string firstLine, string headers, byte[] bodyBytes, CancellationToken ct)
    {
        await WriteHttpHeadAsync(stream, firstLine, headers, ct);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, ct);
    }

    private static async Task WriteHttpHeadAsync(
        Stream stream, string firstLine, string headers, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append(firstLine).Append("\r\n");
        if (!string.IsNullOrEmpty(headers)) head.Append(headers).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        _ => "OK",
    };

    /// <summary>
    /// Rewrites response headers to match the supplied body source: sets Content-Type,
    /// sets/removes Content-Length, strips Transfer-Encoding and Content-Encoding so the
    /// replaced bytes aren't interpreted as chunked or gzipped, and appends extra headers
    /// (Content-Range, Accept-Ranges, Cache-Control) declared by the source.
    /// </summary>
    private static string ApplyBodySourceHeaders(string headers, IoTSpy.Core.Interfaces.IResponseBodySource src)
    {
        static bool StripLine(string line) =>
            line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Content-Encoding:", StringComparison.OrdinalIgnoreCase);

        var lines = (headers ?? string.Empty)
            .Split("\r\n", StringSplitOptions.None)
            .Where(l => !string.IsNullOrEmpty(l) && !StripLine(l))
            .ToList();

        lines.Add($"Content-Type: {src.ContentType}");
        if (src.ContentLength is { } len)
            lines.Add($"Content-Length: {len}");

        foreach (var (name, value) in src.ExtraHeaders)
        {
            // Remove any prior occurrence (case-insensitive) to avoid duplicates.
            lines.RemoveAll(l =>
            {
                var colon = l.IndexOf(':');
                if (colon <= 0) return false;
                return l[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
            });
            lines.Add($"{name}: {value}");
        }

        return string.Join("\r\n", lines);
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

        // Plain-HTTP proxy requests use absolute-form request targets
        // (e.g. "GET http://host:port/path HTTP/1.1") per RFC 7230 §5.3.2, unlike
        // CONNECT/TLS-MITM requests which see origin-form ("GET /path HTTP/1.1").
        // Strip scheme+authority here so Path/Query are always origin-relative —
        // otherwise consumers that build "{scheme}://{host}{path}" (capture display,
        // curl export) end up with the host duplicated into the reconstructed URL.
        if (rawPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(rawPath, UriKind.Absolute, out var absolute))
                rawPath = absolute.PathAndQuery;
        }

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

    /// <summary>
    /// Returns true for MIME types whose bodies are binary and must be base64-encoded for
    /// safe storage in a UTF-8 TEXT column (prevents corruption and SQLite null-byte truncation).
    /// SVG is excluded because it is XML text.
    /// </summary>
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

    /// <summary>
    /// Attempts to decompress a body byte array according to the given Content-Encoding value.
    /// Returns null if the encoding is not recognised or decompression fails.
    /// </summary>
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
        // Round-trip through PFX to get a persistent key handle usable by SChannel on Windows.
        using var ephemeral = X509Certificate2.CreateFromPem(leaf.CertificatePem, leaf.PrivateKeyPem);
        // Do NOT use 'using' here — SslStreamCertificateContext takes ownership of leafCert.
        // Disposing it before the TLS handshake completes causes CryptographicException:
        // "m_safeCertContext is an invalid handle".
        var leafCert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);

        // Include the CA cert explicitly so .NET sends it in the TLS Certificate message.
        // SChannel only discovers chain certs from the Windows cert store; our CA is in
        // SQLite, not the store, so without this iOS receives only the leaf cert and
        // cannot always build the chain from its trust store alone.
        var caCert = X509CertificateLoader.LoadCertificate(PemToBytes(rootCa.CertificatePem));
        var extraCerts = new X509Certificate2Collection(caCert);

        return SslStreamCertificateContext.Create(leafCert, extraCerts, offline: true);
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
