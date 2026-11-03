using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

public interface ICaptureRepository
{
    Task<CapturedRequest> AddAsync(CapturedRequest capture, CancellationToken ct = default);
    Task AddBatchAsync(IReadOnlyList<CapturedRequest> captures, CancellationToken ct = default);
    Task<List<CapturedRequest>> GetPagedAsync(CaptureFilter filter, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(CaptureFilter filter, CancellationToken ct = default);
    Task<CapturedRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(CapturedRequest capture, CancellationToken ct = default);

    /// <summary>Overwrites only the traffic totals of an existing capture (no load, no change
    /// tracking). Used to fold repeated passthrough connections into one row.</summary>
    Task UpdateTrafficStatsAsync(Guid id, long durationMs, long requestBodySize, long responseBodySize,
        string statusMessage, string tlsMetadataJson, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task ClearAsync(Guid? deviceId = null, CancellationToken ct = default);
    Task ClearByFilterAsync(CaptureFilter filter, CancellationToken ct = default);
}

public record CaptureFilter(
    Guid? DeviceId = null,
    string? HostContains = null,
    string? Method = null,
    int? StatusCode = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? BodySearch = null,
    string? ClientIp = null,
    string? HeaderSearch = null
);
