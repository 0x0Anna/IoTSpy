namespace IoTSpy.Core.Models;

/// <summary>
/// A defensive, point-in-time copy of a single host's <see cref="HostBaseline"/> state,
/// produced by <see cref="Interfaces.IAnomalyDetector.SnapshotBaselines"/> while holding
/// that host's internal lock. Safe to read from any thread without further synchronization.
/// Deliberately excludes the sliding request-rate window (<c>RequestTimestamps</c>) — it is
/// not persisted; it rebuilds naturally within <c>RateWindowSeconds</c> of restart.
/// </summary>
public sealed class HostBaselineSnapshot
{
    public required string Host { get; init; }
    public long SampleCount { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }

    public double DurationMean { get; init; }
    public double DurationM2 { get; init; }

    public double SizeMean { get; init; }
    public double SizeM2 { get; init; }

    /// <summary>Defensive copy of the status-code histogram — safe to enumerate freely.</summary>
    public required IReadOnlyDictionary<int, long> StatusCodeCounts { get; init; }
}
