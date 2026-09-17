using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using Prometheus;
using Xunit;

namespace IoTSpy.Api.Tests.Services;

/// <summary>
/// Verifies every IoTSpyMetrics helper is callable without throwing and actually
/// updates the underlying prometheus-net collector. Each collector is registered
/// once into <see cref="Prometheus.Metrics.DefaultRegistry"/> for the process
/// lifetime, so these tests scrape the registry's exported text rather than
/// reaching into private fields.
/// </summary>
public class IoTSpyMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task RecordProxyRequest_DoesNotThrow_AndIncrementsCounter()
    {
        var record = () => IoTSpyMetrics.RecordProxyRequest("http-metrics-test", "200");
        record();

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_proxy_requests_total", text);
        Assert.Contains("protocol=\"http-metrics-test\"", text);
    }

    [Fact]
    public void MeasureScanDuration_ReturnsDisposableTimer_AndDoesNotThrow()
    {
        using var timer = IoTSpyMetrics.MeasureScanDuration("metrics-test-scan");
        Assert.NotNull(timer);
    }

    [Fact]
    public async Task RecordAnomalyAlert_DoesNotThrow_AndIncrementsCounter()
    {
        IoTSpyMetrics.RecordAnomalyAlert(AlertSeverity.Critical);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_anomaly_alerts_total", text);
        Assert.Contains("severity=\"Critical\"", text);
    }

    [Fact]
    public async Task SetCaptureQueueDepth_DoesNotThrow_AndSetsGauge()
    {
        IoTSpyMetrics.SetCaptureQueueDepth(42);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_capture_queue_depth 42", text);
    }

    [Fact]
    public async Task SetActiveCaptures_DoesNotThrow_AndSetsGauge()
    {
        IoTSpyMetrics.SetActiveCaptures(7);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_active_captures_total 7", text);
    }

    [Fact]
    public async Task RecordPluginDecode_DoesNotThrow_AndIncrementsCounter()
    {
        IoTSpyMetrics.RecordPluginDecode("mqtt-metrics-test", true);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_plugin_decode_attempts_total", text);
        Assert.Contains("protocol=\"mqtt-metrics-test\"", text);
        Assert.Contains("success=\"true\"", text);
    }

    [Fact]
    public async Task RecordCapture_DoesNotThrow_AndIncrementsCounter()
    {
        IoTSpyMetrics.RecordCapture("Mqtt-metrics-test");

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_captures_total", text);
        Assert.Contains("protocol=\"Mqtt-metrics-test\"", text);
    }

    [Fact]
    public async Task IncrementAndDecrementSignalRConnections_DoNotThrow_AndUpdateGauge()
    {
        IoTSpyMetrics.IncrementSignalRConnections();
        var afterIncrement = await ScrapeAsync();
        Assert.Contains("iotspy_signalr_connections", afterIncrement);

        IoTSpyMetrics.DecrementSignalRConnections();
        var afterDecrement = await ScrapeAsync();
        Assert.Contains("iotspy_signalr_connections", afterDecrement);
    }
}
