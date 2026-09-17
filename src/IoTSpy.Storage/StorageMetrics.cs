using Prometheus;

namespace IoTSpy.Storage;

/// <summary>
/// Prometheus metrics for the EF Core data layer. Lives in IoTSpy.Storage (rather than
/// IoTSpy.Api.Services.IoTSpyMetrics) since IoTSpy.Api depends on IoTSpy.Storage, not the
/// reverse — the collectors still register into prometheus-net's shared default registry,
/// so they surface on the same /metrics endpoint as the rest of IoTSpyMetrics.
/// </summary>
public static class StorageMetrics
{
    private static readonly Histogram DbQueryDuration = Metrics.CreateHistogram(
        "iotspy_db_query_duration_seconds",
        "Duration of EF Core database command execution",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 12)
        });

    public static void RecordDbQueryDuration(double seconds) =>
        DbQueryDuration.Observe(seconds);
}
