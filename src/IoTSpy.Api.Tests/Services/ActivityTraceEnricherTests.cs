using System.Diagnostics;
using IoTSpy.Api.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace IoTSpy.Api.Tests.Services;

public class ActivityTraceEnricherTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public readonly List<LogEvent> Events = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.With<ActivityTraceEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void Enrich_WithNoCurrentActivity_DoesNotAddTraceProperties()
    {
        Activity.Current = null;
        var (logger, sink) = CreateLogger();

        logger.Information("no activity");

        var evt = Assert.Single(sink.Events);
        Assert.False(evt.Properties.ContainsKey("TraceId"));
        Assert.False(evt.Properties.ContainsKey("SpanId"));
    }

    [Fact]
    public void Enrich_WithCurrentActivity_AddsMatchingTraceIdAndSpanId()
    {
        using var activitySource = new ActivitySource("IoTSpy.Api.Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("test-span");
        Assert.NotNull(activity);

        var (logger, sink) = CreateLogger();
        logger.Information("with activity");

        var evt = Assert.Single(sink.Events);
        var traceId = Assert.IsType<ScalarValue>(evt.Properties["TraceId"]).Value as string;
        var spanId = Assert.IsType<ScalarValue>(evt.Properties["SpanId"]).Value as string;

        Assert.Equal(activity.TraceId.ToHexString(), traceId);
        Assert.Equal(activity.SpanId.ToHexString(), spanId);
    }
}
