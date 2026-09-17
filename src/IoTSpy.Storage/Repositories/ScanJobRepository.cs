using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class ScanJobRepository(IoTSpyDbContext db) : IScanJobRepository
{
    public async Task<ScanJob> AddAsync(ScanJob job, CancellationToken ct = default)
    {
        db.ScanJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public Task<ScanJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.ScanJobs
            .AsNoTracking()
            .Include(j => j.Findings)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<List<ScanJob>> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default) =>
        db.ScanJobs
            .AsNoTracking()
            .Where(j => j.DeviceId == deviceId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public Task<List<ScanJob>> GetAllAsync(
        int page = 1,
        int pageSize = 20,
        ScanStatus? status = null,
        Guid? deviceId = null,
        DateTimeOffset? createdAfter = null,
        CancellationToken ct = default)
    {
        var q = db.ScanJobs.AsNoTracking().AsQueryable();
        if (status.HasValue) q = q.Where(j => j.Status == status.Value);
        if (deviceId.HasValue) q = q.Where(j => j.DeviceId == deviceId.Value);
        if (createdAfter.HasValue) q = q.Where(j => j.CreatedAt > createdAfter.Value);
        return q.OrderByDescending(j => j.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    }

    public Task<int> CountAsync(
        ScanStatus? status = null,
        Guid? deviceId = null,
        DateTimeOffset? createdAfter = null,
        CancellationToken ct = default)
    {
        var q = db.ScanJobs.AsQueryable();
        if (status.HasValue) q = q.Where(j => j.Status == status.Value);
        if (deviceId.HasValue) q = q.Where(j => j.DeviceId == deviceId.Value);
        if (createdAfter.HasValue) q = q.Where(j => j.CreatedAt > createdAfter.Value);
        return q.CountAsync(ct);
    }

    public async Task<ScanJob> UpdateAsync(ScanJob job, CancellationToken ct = default)
    {
        var tracked = db.ChangeTracker.Entries<ScanJob>().FirstOrDefault(e => e.Entity.Id == job.Id);
        if (tracked is not null)
            tracked.CurrentValues.SetValues(job);
        else
            db.ScanJobs.Update(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.ScanJobs.FindAsync([id], ct);
        if (job is not null)
        {
            db.ScanJobs.Remove(job);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task AddFindingAsync(ScanFinding finding, CancellationToken ct = default)
    {
        db.ScanFindings.Add(finding);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddFindingsAsync(IEnumerable<ScanFinding> findings, CancellationToken ct = default)
    {
        db.ScanFindings.AddRange(findings);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<ScanFinding>> GetFindingsAsync(Guid scanJobId, CancellationToken ct = default) =>
        db.ScanFindings
            .AsNoTracking()
            .Where(f => f.ScanJobId == scanJobId)
            .OrderBy(f => f.FoundAt)
            .ToListAsync(ct);

    public Task<ScanFinding?> GetFindingByIdAsync(Guid id, CancellationToken ct = default) =>
        db.ScanFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<ScanFinding> UpdateFindingAsync(ScanFinding finding, CancellationToken ct = default)
    {
        var tracked = db.ChangeTracker.Entries<ScanFinding>().FirstOrDefault(e => e.Entity.Id == finding.Id);
        if (tracked is not null)
            tracked.CurrentValues.SetValues(finding);
        else
            db.ScanFindings.Update(finding);
        await db.SaveChangesAsync(ct);
        return finding;
    }

    public async Task<int> DeleteByFilterAsync(ScanStatus? status, DateTimeOffset? completedBefore, CancellationToken ct = default)
    {
        var query = db.ScanJobs.AsQueryable();
        if (status.HasValue)
            query = query.Where(j => j.Status == status.Value);
        if (completedBefore.HasValue)
            query = query.Where(j => j.CompletedAt.HasValue && j.CompletedAt.Value < completedBefore.Value);
        return await query.ExecuteDeleteAsync(ct);
    }
}
