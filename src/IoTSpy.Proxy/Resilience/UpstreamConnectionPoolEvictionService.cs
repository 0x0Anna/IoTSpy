using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Proxy.Resilience;

/// <summary>
/// Periodically closes idle upstream connections past <see cref="UpstreamConnectionPoolOptions.IdleTimeoutSeconds"/>
/// so <see cref="UpstreamConnectionPool"/> doesn't accumulate connections the peer may have
/// already closed on its end, or that simply aren't being reused anymore.
/// </summary>
public sealed class UpstreamConnectionPoolEvictionService(
    UpstreamConnectionPool pool,
    ILogger<UpstreamConnectionPoolEvictionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                pool.EvictIdle();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "UpstreamConnectionPool: eviction sweep failed");
            }
        }
    }
}
