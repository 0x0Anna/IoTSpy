namespace IoTSpy.Core.Models;

/// <summary>
/// Strongly-typed options for the proxy's shared upstream connection pool.
/// Deserialised from the "ConnectionPool" section of appsettings.json.
/// </summary>
public sealed class UpstreamConnectionPoolOptions
{
    public const string SectionName = "ConnectionPool";

    /// <summary>Maximum idle (checked-in) connections kept per upstream host:port:scheme.</summary>
    public int MaxIdlePerHost { get; set; } = 6;

    /// <summary>Idle connections older than this are closed by the periodic eviction sweep (seconds).</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;
}
