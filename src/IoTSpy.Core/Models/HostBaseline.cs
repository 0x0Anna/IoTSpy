namespace IoTSpy.Core.Models;

/// <summary>
/// Statistical baseline for a single monitored host.
/// Tracks rolling mean and variance using Welford's online algorithm.
/// </summary>
public sealed class HostBaseline
{
    /// <summary>The host this baseline covers.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Total number of observations recorded.</summary>
    public long SampleCount { get; set; }

    /// <summary>
    /// Timestamp of the first observation ever recorded for this host (set once).
    /// Used as the denominator anchor for the historical request-rate calculation,
    /// so persisting/restoring a large <see cref="SampleCount"/> doesn't distort it.
    /// </summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    // Response duration (ms) — Welford running state
    public double DurationMean { get; set; }
    public double DurationM2 { get; set; }   // sum of squared deviations

    // Response size (bytes) — Welford running state
    public double SizeMean { get; set; }
    public double SizeM2 { get; set; }

    // Request rate: track timestamps of last N requests within a sliding window
    public Queue<DateTimeOffset> RequestTimestamps { get; } = new();

    // Status code distribution
    public Dictionary<int, long> StatusCodeCounts { get; } = new();

    /// <summary>Population standard deviation for response duration.</summary>
    public double DurationStdDev =>
        SampleCount > 1 ? Math.Sqrt(DurationM2 / (SampleCount - 1)) : 0;

    /// <summary>Population standard deviation for response size.</summary>
    public double SizeStdDev =>
        SampleCount > 1 ? Math.Sqrt(SizeM2 / (SampleCount - 1)) : 0;

    /// <summary>
    /// Dominant status code (the one seen most often).
    /// Returns 0 if no observations yet.
    /// </summary>
    public int DominantStatusCode =>
        StatusCodeCounts.Count == 0
            ? 0
            : StatusCodeCounts.MaxBy(kv => kv.Value).Key;
}
