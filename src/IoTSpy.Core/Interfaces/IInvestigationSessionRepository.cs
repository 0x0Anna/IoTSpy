using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

public interface IInvestigationSessionRepository
{
    Task<InvestigationSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<InvestigationSession?> GetByShareTokenAsync(string token, CancellationToken ct = default);
    Task<List<InvestigationSession>> GetAllAsync(bool includeInactive = false, Guid? createdByUserId = null, CancellationToken ct = default);
    Task<InvestigationSession> CreateAsync(InvestigationSession session, CancellationToken ct = default);
    Task UpdateAsync(InvestigationSession session, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // SessionCapture join table
    Task AddCaptureAsync(SessionCapture sc, CancellationToken ct = default);
    Task RemoveCaptureAsync(Guid sessionId, Guid captureId, CancellationToken ct = default);
    /// <summary>Most recently added captures for a session, newest first. <paramref name="limit"/>
    /// applies at the database level (not an in-memory truncation) — pass it whenever the full
    /// history isn't needed, since captures carry full request/response bodies.</summary>
    Task<List<SessionCapture>> GetSessionCapturesAsync(Guid sessionId, int? limit = null, CancellationToken ct = default);
    Task<bool> ContainsCaptureAsync(Guid sessionId, Guid captureId, CancellationToken ct = default);
}
