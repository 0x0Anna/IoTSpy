using Prometheus;

namespace IoTSpy.Proxy.Resilience;

/// <summary>
/// Prometheus metrics for the upstream connection pool. Lives in IoTSpy.Proxy alongside
/// the pool itself; collectors register into prometheus-net's shared default registry, so
/// they surface on the same /metrics endpoint as IoTSpy.Storage's StorageMetrics.
/// </summary>
public static class ProxyMetrics
{
    public static readonly Counter PoolHits = Metrics.CreateCounter(
        "iotspy_proxy_pool_hits_total",
        "Upstream connection checkouts served from the pool instead of opening a new connection");

    public static readonly Counter PoolMisses = Metrics.CreateCounter(
        "iotspy_proxy_pool_misses_total",
        "Upstream connection checkouts that required opening a new connection");

    public static readonly Gauge PoolSize = Metrics.CreateGauge(
        "iotspy_proxy_pool_size",
        "Total idle upstream connections currently held in the pool across all hosts");
}
