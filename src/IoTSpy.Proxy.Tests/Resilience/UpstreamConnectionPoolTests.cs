using System.Net;
using System.Net.Sockets;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IoTSpy.Proxy.Tests.Resilience;

/// <summary>
/// Exercises UpstreamConnectionPool against a real local TCP listener standing in for the
/// upstream host — the pool's checkout/return/discard/eviction behavior is fundamentally
/// about real socket state, so a plain-HTTP loopback listener gives higher-fidelity coverage
/// here than mocking the connection layer.
/// </summary>
public class UpstreamConnectionPoolTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly List<TcpClient> _acceptedByServer = [];
    private readonly CancellationTokenSource _acceptCts = new();

    public UpstreamConnectionPoolTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_acceptCts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                lock (_acceptedByServer) _acceptedByServer.Add(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _acceptCts.Cancel();
        _listener.Stop();
        lock (_acceptedByServer)
        {
            foreach (var c in _acceptedByServer) c.Dispose();
        }
    }

    private static UpstreamConnectionPool CreatePool(UpstreamConnectionPoolOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProxyResilience(new ResilienceOptions(), options ?? new UpstreamConnectionPoolOptions());
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<UpstreamConnectionPool>();
    }

    private static int LocalPortOf(Stream stream) =>
        ((IPEndPoint)((NetworkStream)stream).Socket.LocalEndPoint!).Port;

    [Fact]
    public async Task CheckoutAsync_NoIdleConnections_CreatesNewConnection()
    {
        var pool = CreatePool();

        var stream = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);

        Assert.True(stream.CanRead);
        pool.Discard(stream);
    }

    [Fact]
    public async Task Return_ThenCheckout_ReusesSameConnection()
    {
        var pool = CreatePool();

        var first = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var firstPort = LocalPortOf(first);
        pool.Return("127.0.0.1", _port, isTls: false, first);

        var second = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var secondPort = LocalPortOf(second);

        Assert.Same(first, second);
        Assert.Equal(firstPort, secondPort);
        pool.Discard(second);
    }

    [Fact]
    public async Task Discard_ThenCheckout_CreatesNewConnection()
    {
        var pool = CreatePool();

        var first = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var firstPort = LocalPortOf(first);
        pool.Discard(first);

        var second = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var secondPort = LocalPortOf(second);

        Assert.NotEqual(firstPort, secondPort);
        pool.Discard(second);
    }

    [Fact]
    public async Task Return_BeyondMaxIdle_DisposesExcessRatherThanPooling()
    {
        var pool = CreatePool(new UpstreamConnectionPoolOptions { MaxIdlePerHost = 1 });

        var first = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var second = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var firstPort = LocalPortOf(first);
        var secondPort = LocalPortOf(second);

        pool.Return("127.0.0.1", _port, isTls: false, first);
        pool.Return("127.0.0.1", _port, isTls: false, second); // pool already has 1 idle — this one gets disposed, not pooled

        var third = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var thirdPort = LocalPortOf(third);

        // The reused connection must be "first" (the one actually kept in the pool) — the
        // pool cap means "second" was disposed on return, not silently retained anyway.
        Assert.Equal(firstPort, thirdPort);
        Assert.NotEqual(secondPort, thirdPort);
        pool.Discard(third);
    }

    [Fact]
    public async Task EvictIdle_ClosesConnectionsPastIdleTimeout()
    {
        var pool = CreatePool(new UpstreamConnectionPoolOptions { IdleTimeoutSeconds = 0 });

        var stream = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var evictedPort = LocalPortOf(stream);
        pool.Return("127.0.0.1", _port, isTls: false, stream);

        await Task.Delay(50, TestContext.Current.CancellationToken); // ensure LastUsedUtc is already before the 0s cutoff
        pool.EvictIdle();

        var next = await pool.CheckoutAsync("127.0.0.1", _port, isTls: false, TestContext.Current.CancellationToken);
        var nextPort = LocalPortOf(next);

        Assert.NotEqual(evictedPort, nextPort);
        pool.Discard(next);
    }

    [Fact]
    public async Task Discard_UnknownStream_DoesNotThrow()
    {
        var pool = CreatePool();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port, TestContext.Current.CancellationToken);
        var stream = tcp.GetStream();

        var ex = Record.Exception(() => pool.Discard(stream));

        Assert.Null(ex);
    }
}
