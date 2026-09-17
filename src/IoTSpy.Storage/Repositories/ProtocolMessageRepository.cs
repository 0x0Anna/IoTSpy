using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class ProtocolMessageRepository(IoTSpyDbContext db) : IProtocolMessageRepository
{
    public async Task AddBatchAsync(IReadOnlyList<PersistedProtocolMessage> messages, CancellationToken ct = default)
    {
        db.ProtocolMessages.AddRange(messages);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<PersistedProtocolMessage>> GetByDeviceIdAsync(
        Guid deviceId, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken ct = default)
    {
        var query = ApplyRange(db.ProtocolMessages.AsNoTracking().Where(m => m.DeviceId == deviceId), from, to)
            .OrderByDescending(m => m.Timestamp);
        return ApplyLimit(query, limit).ToListAsync(ct);
    }

    public Task<List<PersistedProtocolMessage>> GetByDeviceIdsAsync(
        IEnumerable<Guid> deviceIds, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken ct = default)
    {
        var ids = deviceIds.ToList();
        var query = ApplyRange(db.ProtocolMessages.AsNoTracking().Where(m => m.DeviceId != null && ids.Contains(m.DeviceId!.Value)), from, to)
            .OrderByDescending(m => m.Timestamp);
        return ApplyLimit(query, limit).ToListAsync(ct);
    }

    private static IQueryable<PersistedProtocolMessage> ApplyLimit(IQueryable<PersistedProtocolMessage> q, int? limit) =>
        limit.HasValue ? q.Take(limit.Value) : q;

    private static IQueryable<PersistedProtocolMessage> ApplyRange(
        IQueryable<PersistedProtocolMessage> q, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from.HasValue) q = q.Where(m => m.Timestamp >= from.Value);
        if (to.HasValue) q = q.Where(m => m.Timestamp <= to.Value);
        return q;
    }
}
