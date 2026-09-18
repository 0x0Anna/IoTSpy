using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class HostBaselineRepository(IoTSpyDbContext db) : IHostBaselineRepository
{
    public async Task UpsertAsync(HostBaselineRecord record, CancellationToken ct = default)
    {
        var existing = await db.HostBaselines.FirstOrDefaultAsync(r => r.Host == record.Host, ct);
        if (existing is null)
        {
            db.HostBaselines.Add(record);
        }
        else
        {
            existing.SampleCount = record.SampleCount;
            existing.FirstSeenAt = record.FirstSeenAt;
            existing.DurationMean = record.DurationMean;
            existing.DurationM2 = record.DurationM2;
            existing.SizeMean = record.SizeMean;
            existing.SizeM2 = record.SizeM2;
            existing.StatusCodeCountsJson = record.StatusCodeCountsJson;
            existing.UpdatedAt = record.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<List<HostBaselineRecord>> GetAllAsync(CancellationToken ct = default) =>
        db.HostBaselines.AsNoTracking().ToListAsync(ct);

    public async Task DeleteAsync(string host, CancellationToken ct = default)
    {
        await db.HostBaselines.Where(r => r.Host == host).ExecuteDeleteAsync(ct);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        db.HostBaselines.Where(r => r.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);
}
