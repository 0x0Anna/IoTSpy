using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace IoTSpy.Api.Services;

/// <summary>
/// Correlates Serilog log lines with OpenTelemetry traces (backlog #51) by copying the
/// current <see cref="Activity"/>'s TraceId/SpanId onto every log event as
/// <c>TraceId</c>/<c>SpanId</c> properties. A log line and a trace span for the same
/// request can then be joined on TraceId in whatever backend ingests both (e.g. an OTLP
/// collector plus a log sink, or Seq alongside a tracing UI).
/// </summary>
/// <remarks>
/// A lightweight custom enricher was chosen over <c>Serilog.Enrichers.Span</c> — it needs
/// no new NuGet dependency, and ASP.NET Core's hosting pipeline already populates
/// <see cref="Activity.Current"/> for every request (via the built-in
/// "Microsoft.AspNetCore.Hosting.HttpRequestIn" activity source) once something is
/// listening for it, which the OpenTelemetry ASP.NET Core instrumentation does when
/// <c>Otel:Enabled</c> is true. When tracing is disabled, <see cref="Activity.Current"/>
/// is simply null and this enricher is a no-op, so it is always safe to register.
/// </remarks>
public class ActivityTraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("TraceId", activity.TraceId.ToHexString()));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty("SpanId", activity.SpanId.ToHexString()));
    }
}
