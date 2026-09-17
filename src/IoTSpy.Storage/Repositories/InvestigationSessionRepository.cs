using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class InvestigationSessionRepository(IoTSpyDbContext db) : IInvestigationSessionRepository
{
    public async Task<InvestigationSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.InvestigationSessions
            .Include(s => s.Activities.OrderByDescending(a => a.Timestamp).Take(50))
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<InvestigationSession?> GetByShareTokenAsync(string token, CancellationToken ct = default)
        => await db.InvestigationSessions
            .FirstOrDefaultAsync(s => s.ShareToken == token && s.IsActive, ct);

    public async Task<List<InvestigationSession>> GetAllAsync(bool includeInactive = false, Guid? createdByUserId = null, CancellationToken ct = default)
        => await db.InvestigationSessions
            .Where(s => includeInactive || s.IsActive)
            .Where(s => createdByUserId == null || s.CreatedByUserId == createdByUserId.Value)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    public async Task<InvestigationSession> CreateAsync(InvestigationSession session, CancellationToken ct = default)
    {
        db.InvestigationSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task UpdateAsync(InvestigationSession session, CancellationToken ct = default)
    {
        db.InvestigationSessions.Update(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.InvestigationSessions.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task AddCaptureAsync(SessionCapture sc, CancellationToken ct = default)
    {
        db.SessionCaptures.Add(sc);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveCaptureAsync(Guid sessionId, Guid captureId, CancellationToken ct = default)
    {
        await db.SessionCaptures
            .Where(sc => sc.SessionId == sessionId && sc.CaptureId == captureId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<List<SessionCapture>> GetSessionCapturesAsync(Guid sessionId, int? limit = null, CancellationToken ct = default)
    {
        var query = db.SessionCaptures
            .Include(sc => sc.Capture)
            .Where(sc => sc.SessionId == sessionId)
            .OrderByDescending(sc => sc.AddedAt)
            .AsQueryable();
        if (limit.HasValue) query = query.Take(limit.Value);
        return await query.ToListAsync(ct);
    }

    public async Task<bool> ContainsCaptureAsync(Guid sessionId, Guid captureId, CancellationToken ct = default)
        => await db.SessionCaptures
            .AnyAsync(sc => sc.SessionId == sessionId && sc.CaptureId == captureId, ct);
}
