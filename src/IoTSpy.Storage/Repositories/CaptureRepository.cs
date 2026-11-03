using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class CaptureRepository(IoTSpyDbContext db) : ICaptureRepository
{
    public async Task<CapturedRequest> AddAsync(CapturedRequest capture, CancellationToken ct = default)
    {
        db.Captures.Add(capture);
        await db.SaveChangesAsync(ct);
        return capture;
    }

    public async Task AddBatchAsync(IReadOnlyList<CapturedRequest> captures, CancellationToken ct = default)
    {
        db.Captures.AddRange(captures);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<CapturedRequest>> GetPagedAsync(CaptureFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        return ApplyFilter(filter)
            .OrderByDescending(c => c.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public Task<int> CountAsync(CaptureFilter filter, CancellationToken ct = default) =>
        ApplyFilter(filter).CountAsync(ct);

    public Task<CapturedRequest?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Captures.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task UpdateAsync(CapturedRequest capture, CancellationToken ct = default)
    {
        db.Captures.Update(capture);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateTrafficStatsAsync(Guid id, long durationMs, long requestBodySize, long responseBodySize,
        string statusMessage, string tlsMetadataJson, CancellationToken ct = default) =>
        await db.Captures.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.DurationMs, durationMs)
            .SetProperty(c => c.RequestBodySize, requestBodySize)
            .SetProperty(c => c.ResponseBodySize, responseBodySize)
            .SetProperty(c => c.StatusMessage, statusMessage)
            .SetProperty(c => c.TlsMetadataJson, tlsMetadataJson), ct) > 0;

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var capture = await db.Captures.FindAsync([id], ct);
        if (capture is not null)
        {
            db.Captures.Remove(capture);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ClearAsync(Guid? deviceId = null, CancellationToken ct = default)
    {
        var query = deviceId.HasValue
            ? db.Captures.Where(c => c.DeviceId == deviceId)
            : db.Captures.AsQueryable();
        db.Captures.RemoveRange(query);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearByFilterAsync(CaptureFilter filter, CancellationToken ct = default)
    {
        var q = db.Captures.AsQueryable();
        if (filter.DeviceId.HasValue)
            q = q.Where(c => c.DeviceId == filter.DeviceId);
        if (!string.IsNullOrEmpty(filter.HostContains))
            q = q.Where(c => c.Host.Contains(filter.HostContains));
        if (!string.IsNullOrEmpty(filter.Method))
            q = q.Where(c => c.Method == filter.Method.ToUpper());
        if (filter.StatusCode.HasValue)
            q = q.Where(c => c.StatusCode == filter.StatusCode);
        if (filter.From.HasValue)
            q = q.Where(c => c.Timestamp >= filter.From);
        if (filter.To.HasValue)
            q = q.Where(c => c.Timestamp <= filter.To);
        if (!string.IsNullOrEmpty(filter.ClientIp))
            q = q.Where(c => c.ClientIp.Contains(filter.ClientIp));
        await q.ExecuteDeleteAsync(ct);
    }

    private IQueryable<CapturedRequest> ApplyFilter(CaptureFilter filter)
    {
        var q = db.Captures.AsNoTracking();
        if (filter.DeviceId.HasValue)
            q = q.Where(c => c.DeviceId == filter.DeviceId);
        if (!string.IsNullOrEmpty(filter.HostContains))
            q = q.Where(c => c.Host.Contains(filter.HostContains));
        if (!string.IsNullOrEmpty(filter.Method))
            q = q.Where(c => c.Method == filter.Method.ToUpper());
        if (filter.StatusCode.HasValue)
            q = q.Where(c => c.StatusCode == filter.StatusCode);
        if (filter.From.HasValue)
            q = q.Where(c => c.Timestamp >= filter.From);
        if (filter.To.HasValue)
            q = q.Where(c => c.Timestamp <= filter.To);
        if (!string.IsNullOrEmpty(filter.BodySearch))
            q = q.Where(c => c.RequestBody.Contains(filter.BodySearch) || c.ResponseBody.Contains(filter.BodySearch));
        if (!string.IsNullOrEmpty(filter.ClientIp))
            q = q.Where(c => c.ClientIp.Contains(filter.ClientIp));
        if (!string.IsNullOrEmpty(filter.HeaderSearch))
            q = q.Where(c => c.RequestHeaders.Contains(filter.HeaderSearch) || c.ResponseHeaders.Contains(filter.HeaderSearch));
        return q;
    }
}
