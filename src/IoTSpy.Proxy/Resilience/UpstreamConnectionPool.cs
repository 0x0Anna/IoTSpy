using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using IoTSpy.Core.Models;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace IoTSpy.Proxy.Resilience;

/// <summary>
/// Shared pool of upstream TCP/TLS connections, keyed by (host, port, isTls) and reused
/// across every client device the proxy serves — not scoped per client connection.
///
/// Previously each client-to-proxy connection opened its own dedicated upstream connection
/// for its entire lifetime. A single page load or app session fans out to far more
/// simultaneous *new* upstream connections than a direct (non-proxied) browser session would
/// ever create, since browsers pool and reuse a small number of connections per host — this
/// showed up as excess connection load on small/embedded upstream devices under real traffic.
/// Pooling here mirrors that browser-side reuse on the proxy's upstream side instead.
/// </summary>
public interface IUpstreamConnectionPool
{
    Task<Stream> CheckoutAsync(string host, int port, bool isTls, CancellationToken ct);

    /// <summary>Returns a still-usable connection to the pool for reuse by any client.</summary>
    void Return(string host, int port, bool isTls, Stream stream);

    /// <summary>Discards a connection that must not be reused (Connection: close, WebSocket upgrade, error).</summary>
    void Discard(Stream stream);
}

internal sealed record PooledConnection(TcpClient TcpClient, Stream Stream, DateTime LastUsedUtc);

public sealed class UpstreamConnectionPool(
    IPerHostConnectPipelineCache perHostPipelines,
    ResiliencePipelineProvider<string> connectPipelineProvider,
    UpstreamConnectionPoolOptions options,
    ILogger<UpstreamConnectionPool> logger) : IUpstreamConnectionPool
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>> _idle = new();
    private readonly ConcurrentDictionary<Stream, (string Key, TcpClient Tcp)> _checkedOut = new();

    private static string Key(string host, int port, bool isTls) =>
        $"{host}:{port}:{(isTls ? "tls" : "plain")}";

    public async Task<Stream> CheckoutAsync(string host, int port, bool isTls, CancellationToken ct)
    {
        var key = Key(host, port, isTls);
        if (_idle.TryGetValue(key, out var queue))
        {
            while (queue.TryDequeue(out var pooled))
            {
                if (IsAlive(pooled.TcpClient))
                {
                    ProxyMetrics.PoolHits.Inc();
                    _checkedOut[pooled.Stream] = (key, pooled.TcpClient);
                    return pooled.Stream;
                }
                pooled.Stream.Dispose();
                pooled.TcpClient.Dispose();
            }
        }

        ProxyMetrics.PoolMisses.Inc();
        var (tcp, stream) = await CreateAsync(host, port, isTls, ct);
        _checkedOut[stream] = (key, tcp);
        return stream;
    }

    public void Return(string host, int port, bool isTls, Stream stream)
    {
        if (!_checkedOut.TryRemove(stream, out var entry))
        {
            stream.Dispose();
            return;
        }

        var queue = _idle.GetOrAdd(entry.Key, _ => new ConcurrentQueue<PooledConnection>());
        if (queue.Count >= options.MaxIdlePerHost)
        {
            stream.Dispose();
            entry.Tcp.Dispose();
            return;
        }

        queue.Enqueue(new PooledConnection(entry.Tcp, stream, DateTime.UtcNow));
    }

    public void Discard(Stream stream)
    {
        if (_checkedOut.TryRemove(stream, out var entry))
            entry.Tcp.Dispose();
        stream.Dispose();
    }

    private static bool IsAlive(TcpClient tcp)
    {
        try
        {
            var socket = tcp.Client;
            if (socket is null || !socket.Connected) return false;
            // A socket that's readable with zero bytes available means the peer closed its
            // end (or sent a bare FIN) — the classic signature of a dead keep-alive connection.
            var readable = socket.Poll(0, SelectMode.SelectRead);
            return !(readable && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(TcpClient tcp, Stream stream)> CreateAsync(string host, int port, bool isTls, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await perHostPipelines.GetPipeline(host, port).ExecuteAsync(async token =>
        {
            await tcp.ConnectAsync(host, port, token);
            return tcp;
        }, ct);

        if (!isTls)
            return (tcp, tcp.GetStream());

        var ssl = new SslStream(tcp.GetStream());
        await connectPipelineProvider.GetPipeline(ProxyResiliencePipelines.TlsPipelineKey).ExecuteAsync(async token =>
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, token);
        }, ct);
        return (tcp, ssl);
    }

    /// <summary>Closes idle connections past the configured timeout. Called by the periodic eviction sweep.</summary>
    internal void EvictIdle()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-options.IdleTimeoutSeconds);
        var totalIdle = 0;
        foreach (var (key, queue) in _idle)
        {
            var kept = new List<PooledConnection>();
            var evicted = 0;
            while (queue.TryDequeue(out var pooled))
            {
                if (pooled.LastUsedUtc < cutoff || !IsAlive(pooled.TcpClient))
                {
                    pooled.Stream.Dispose();
                    pooled.TcpClient.Dispose();
                    evicted++;
                }
                else
                {
                    kept.Add(pooled);
                }
            }
            foreach (var k in kept) queue.Enqueue(k);
            totalIdle += kept.Count;
            if (evicted > 0)
                logger.LogDebug("UpstreamConnectionPool: evicted {Count} idle/dead connection(s) for {Key}", evicted, key);
        }
        ProxyMetrics.PoolSize.Set(totalIdle);
    }
}
