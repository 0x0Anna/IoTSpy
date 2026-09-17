using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

public interface IProtocolMessageRepository
{
    Task AddBatchAsync(IReadOnlyList<PersistedProtocolMessage> messages, CancellationToken ct = default);

    /// <param name="limit">Caps the result at the database level, newest first. Omit for the
    /// full range — callers rendering an overview (e.g. a report) should always pass one, since
    /// message volume is unbounded over a device's lifetime.</param>
    Task<List<PersistedProtocolMessage>> GetByDeviceIdAsync(
        Guid deviceId, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken ct = default);

    /// <summary>Aggregates messages across every device referenced by a session's captures —
    /// used by the session-scoped report, since sessions have no direct device link.</summary>
    /// <param name="limit">See <see cref="GetByDeviceIdAsync"/>.</param>
    Task<List<PersistedProtocolMessage>> GetByDeviceIdsAsync(
        IEnumerable<Guid> deviceIds, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken ct = default);
}
