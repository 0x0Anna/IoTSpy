using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

/// <summary>
/// Persists periodic checkpoints of <see cref="IAnomalyDetector"/> host baselines so they
/// survive an API restart instead of resetting to a cold, un-warmed-up state.
/// </summary>
public interface IHostBaselineRepository
{
    /// <summary>Inserts or updates the persisted row for <paramref name="record"/>.Host.</summary>
    Task UpsertAsync(HostBaselineRecord record, CancellationToken ct = default);

    /// <summary>Returns every persisted host baseline.</summary>
    Task<List<HostBaselineRecord>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Deletes the persisted row for the given host, if any.</summary>
    Task DeleteAsync(string host, CancellationToken ct = default);

    /// <summary>Deletes all persisted rows last updated before <paramref name="cutoff"/>. Returns the count deleted.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
