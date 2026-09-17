using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

public interface IProtocolMessageRepository
{
    Task AddBatchAsync(IReadOnlyList<PersistedProtocolMessage> messages, CancellationToken ct = default);

    Task<List<PersistedProtocolMessage>> GetByDeviceIdAsync(
        Guid deviceId, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default);

    /// <summary>Aggregates messages across every device referenced by a session's captures —
    /// used by the session-scoped report, since sessions have no direct device link.</summary>
    Task<List<PersistedProtocolMessage>> GetByDeviceIdsAsync(
        IEnumerable<Guid> deviceIds, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default);
}
