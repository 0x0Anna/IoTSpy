using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

/// <summary>
/// Records traffic observations for a host and detects statistical anomalies
/// against a rolling baseline.
/// </summary>
public interface IAnomalyDetector
{
    /// <summary>
    /// Records one traffic observation for the given host and returns any
    /// anomalies detected compared to the established baseline.
    /// </summary>
    /// <param name="host">The target host (e.g. "api.example.com").</param>
    /// <param name="responseDurationMs">Round-trip duration in milliseconds.</param>
    /// <param name="responseSizeBytes">Size of the response body in bytes.</param>
    /// <param name="statusCode">HTTP status code received.</param>
    /// <returns>Zero or more anomaly alerts triggered by this observation.</returns>
    IReadOnlyList<AnomalyAlert> Record(
        string host,
        double responseDurationMs,
        long responseSizeBytes,
        int statusCode);

    /// <summary>
    /// Returns a snapshot of the current baseline statistics for all hosts.
    /// </summary>
    IReadOnlyDictionary<string, HostBaseline> GetBaselines();

    /// <summary>
    /// Clears the baseline and observation window for the specified host.
    /// </summary>
    void Reset(string host);

    /// <summary>
    /// Returns a defensive, point-in-time snapshot of every host's baseline statistics,
    /// safe to read from any thread. Each host's data is copied out while holding that
    /// host's internal lock (the same lock <see cref="Record"/> uses), so callers never
    /// observe torn state and never need to synchronize with the detector themselves.
    /// The sliding request-rate window is intentionally excluded (not worth persisting).
    /// </summary>
    IReadOnlyList<HostBaselineSnapshot> SnapshotBaselines();

    /// <summary>
    /// Restores previously-persisted baseline checkpoints into the live in-memory state,
    /// bypassing <see cref="Record"/>'s normal warm-up gate — a restored host does not
    /// need to re-earn its warm-up sample count. Intended for startup recovery only.
    /// </summary>
    void Seed(IEnumerable<HostBaselineRecord> records);
}
