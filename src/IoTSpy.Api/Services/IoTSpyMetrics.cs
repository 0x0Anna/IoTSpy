using IoTSpy.Core.Interfaces;
using Prometheus;

namespace IoTSpy.Api.Services;

public static class IoTSpyMetrics
{
    private static readonly Counter ProxyRequests = Metrics.CreateCounter(
        "iotspy_proxy_requests_total",
        "Total proxy requests intercepted",
        labelNames: ["protocol", "status"]);

    private static readonly Histogram ScanDuration = Metrics.CreateHistogram(
        "iotspy_scan_duration_seconds",
        "Duration of network scans",
        new HistogramConfiguration
        {
            LabelNames = ["scan_type"],
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 10)
        });

    private static readonly Counter AnomalyAlerts = Metrics.CreateCounter(
        "iotspy_anomaly_alerts_total",
        "Total anomaly alerts raised",
        labelNames: ["severity"]);

    private static readonly Gauge CaptureQueueDepth = Metrics.CreateGauge(
        "iotspy_capture_queue_depth",
        "Current number of entries in the packet capture ring buffer");

    private static readonly Gauge ActiveCaptures = Metrics.CreateGauge(
        "iotspy_active_captures_total",
        "Number of currently active capture sessions");

    private static readonly Counter PluginDecodeAttempts = Metrics.CreateCounter(
        "iotspy_plugin_decode_attempts_total",
        "Plugin decoder invocations",
        labelNames: ["protocol", "success"]);

    private static readonly Counter CapturesPersisted = Metrics.CreateCounter(
        "iotspy_captures_total",
        "Total captures persisted, by protocol",
        labelNames: ["protocol"]);

    private static readonly Gauge SignalRConnections = Metrics.CreateGauge(
        "iotspy_signalr_connections",
        "Current number of active SignalR hub connections");

    public static void RecordProxyRequest(string protocol, string status) =>
        ProxyRequests.WithLabels(protocol, status).Inc();

    public static IDisposable MeasureScanDuration(string scanType) =>
        ScanDuration.WithLabels(scanType).NewTimer();

    public static void RecordAnomalyAlert(AlertSeverity severity) =>
        AnomalyAlerts.WithLabels(severity.ToString()).Inc();

    public static void SetCaptureQueueDepth(double depth) =>
        CaptureQueueDepth.Set(depth);

    public static void SetActiveCaptures(double count) =>
        ActiveCaptures.Set(count);

    public static void RecordPluginDecode(string protocol, bool success) =>
        PluginDecodeAttempts.WithLabels(protocol, success ? "true" : "false").Inc();

    public static void RecordCapture(string protocol) =>
        CapturesPersisted.WithLabels(protocol).Inc();

    public static void IncrementSignalRConnections() =>
        SignalRConnections.Inc();

    public static void DecrementSignalRConnections() =>
        SignalRConnections.Dec();
}
