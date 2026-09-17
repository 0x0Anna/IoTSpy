namespace IoTSpy.Api.Services;

/// <summary>
/// OpenTelemetry tracing configuration (backlog #51). Opt-in — <see cref="Enabled"/>
/// defaults to false so existing deployments aren't forced to stand up an OTLP collector.
/// When disabled, Program.cs skips registering the tracing pipeline entirely rather than
/// wiring a no-op exporter.
/// </summary>
public class OtelOptions
{
    public const string SectionName = "Otel";

    public bool Enabled { get; set; } = false;

    /// <summary>Reported as the `service.name` resource attribute on every exported span.</summary>
    public string ServiceName { get; set; } = "IoTSpy.Api";

    /// <summary>OTLP/gRPC collector endpoint, e.g. http://localhost:4317.</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
}
