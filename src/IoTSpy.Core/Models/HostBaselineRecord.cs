namespace IoTSpy.Core.Models;

/// <summary>
/// EF-mapped storage entity for a persisted <see cref="HostBaseline"/> checkpoint.
/// Deliberately distinct from the in-memory <see cref="HostBaseline"/> model (whose
/// <c>Host</c> is init-only and whose collection properties are get-only auto-initialized),
/// which is awkward to map directly with EF Core.
/// </summary>
public sealed class HostBaselineRecord
{
    /// <summary>
    /// The host this baseline covers (SNI/Host-header value). Primary key.
    /// Bounded length: this is attacker-influenceable input (a probing device can present
    /// an arbitrary SNI/Host header), so the column must not be unbounded.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public long SampleCount { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }

    public double DurationMean { get; set; }
    public double DurationM2 { get; set; }

    public double SizeMean { get; set; }
    public double SizeM2 { get; set; }

    /// <summary>JSON-serialized <c>Dictionary&lt;int,long&gt;</c> status-code histogram.</summary>
    public string StatusCodeCountsJson { get; set; } = "{}";

    /// <summary>When this row was last checkpointed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
