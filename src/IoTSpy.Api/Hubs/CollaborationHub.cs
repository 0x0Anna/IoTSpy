using System.Collections.Concurrent;
using IoTSpy.Api.Services;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IoTSpy.Api.Hubs;

/// <summary>
/// SignalR hub for investigation-session collaboration.
/// Handles session groups, presence tracking (15.4), viewer restrictions (15.3),
/// annotation broadcasts (15.2), and activity feed (15.5).
/// </summary>
[Authorize]
public class CollaborationHub(
    IInvestigationSessionRepository sessionRepo,
    ICaptureAnnotationRepository annotationRepo,
    ISessionActivityRepository activityRepo) : Hub
{
    // ── In-memory presence store: sessionId → set of { connectionId, userId, username } ──
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PresenceEntry>> _presence = new();

    private string? UserId => Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string Username => Context.User?.Identity?.Name ?? Context.User?.FindFirst("sub")?.Value ?? "unknown";
    private bool IsViewer => Context.User?.IsInRole(UserRole.Viewer.ToString()) ?? false;

    // ── Session group subscriptions ──────────────────────────────────────────────

    public override Task OnConnectedAsync()
    {
        IoTSpyMetrics.IncrementSignalRConnections();
        return base.OnConnectedAsync();
    }

    public async Task JoinSession(string sessionId)
    {
        var session = await sessionRepo.GetByIdAsync(Guid.Parse(sessionId));
        if (session is null || !session.IsActive)
        {
            await Clients.Caller.SendAsync("Error", "Session not found or inactive");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

        // Record presence
        var entries = _presence.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, PresenceEntry>());
        entries[Context.ConnectionId] = new PresenceEntry(UserId ?? "", Username, DateTimeOffset.UtcNow);

        // Broadcast updated presence list to the session
        await BroadcastPresenceAsync(sessionId);

        // Log "joined session" activity (viewers can join)
        if (Guid.TryParse(UserId, out var uid))
        {
            var activity = new SessionActivity
            {
                SessionId = session.Id,
                UserId = uid,
                Username = Username,
                Action = "joined session"
            };
            await activityRepo.AddAsync(activity);
            await Clients.Group(SessionGroup(sessionId))
                .SendAsync("SessionActivity", ActivityToDto(activity));
        }
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
        RemovePresence(sessionId, Context.ConnectionId);
        await BroadcastPresenceAsync(sessionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        IoTSpyMetrics.DecrementSignalRConnections();

        // Clean up presence from all sessions this connection was in
        foreach (var sessionId in _presence.Keys)
        {
            RemovePresence(sessionId, Context.ConnectionId);
            await BroadcastPresenceAsync(sessionId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ── Annotations (15.2) — Viewer role restricted ──────────────────────────────

    public async Task AddAnnotation(string sessionId, string captureId, string note, string? tags)
    {
        if (IsViewer)
        {
            await Clients.Caller.SendAsync("Error", "Viewer role cannot add annotations");
            return;
        }

        if (!Guid.TryParse(UserId, out var uid))
        {
            await Clients.Caller.SendAsync("Error", "Unauthenticated");
            return;
        }

        var annotation = new CaptureAnnotation
        {
            SessionId = Guid.Parse(sessionId),
            CaptureId = Guid.Parse(captureId),
            UserId = uid,
            Username = Username,
            Note = note,
            Tags = tags
        };
        await annotationRepo.AddAsync(annotation);

        await Clients.Group(SessionGroup(sessionId))
            .SendAsync("AnnotationAdded", AnnotationToDto(annotation));

        // Activity log
        var activity = new SessionActivity
        {
            SessionId = Guid.Parse(sessionId),
            UserId = uid,
            Username = Username,
            Action = "added annotation",
            Details = $"Capture {captureId}: {note[..Math.Min(note.Length, 60)]}"
        };
        await activityRepo.AddAsync(activity);
        await Clients.Group(SessionGroup(sessionId))
            .SendAsync("SessionActivity", ActivityToDto(activity));
    }

    public async Task UpdateAnnotation(string annotationId, string note, string? tags)
    {
        if (IsViewer)
        {
            await Clients.Caller.SendAsync("Error", "Viewer role cannot update annotations");
            return;
        }

        var annotation = await annotationRepo.GetByIdAsync(Guid.Parse(annotationId));
        if (annotation is null) return;

        annotation.Note = note;
        annotation.Tags = tags;
        annotation.UpdatedAt = DateTimeOffset.UtcNow;
        await annotationRepo.UpdateAsync(annotation);

        await Clients.Group(SessionGroup(annotation.SessionId.ToString()))
            .SendAsync("AnnotationUpdated", AnnotationToDto(annotation));
    }

    public async Task DeleteAnnotation(string annotationId)
    {
        if (IsViewer)
        {
            await Clients.Caller.SendAsync("Error", "Viewer role cannot delete annotations");
            return;
        }

        var annotation = await annotationRepo.GetByIdAsync(Guid.Parse(annotationId));
        if (annotation is null) return;

        var sessionId = annotation.SessionId.ToString();
        await annotationRepo.DeleteAsync(Guid.Parse(annotationId));

        await Clients.Group(SessionGroup(sessionId))
            .SendAsync("AnnotationDeleted", new { id = annotationId });
    }

    // ── Presence broadcast helper ────────────────────────────────────────────────

    private async Task BroadcastPresenceAsync(string sessionId)
    {
        if (!_presence.TryGetValue(sessionId, out var entries)) return;

        var list = entries.Values.Select(e => new { e.UserId, e.Username, e.JoinedAt }).ToList();
        await Clients.Group(SessionGroup(sessionId)).SendAsync("PresenceUpdated", list);
    }

    private static void RemovePresence(string sessionId, string connectionId)
    {
        if (_presence.TryGetValue(sessionId, out var entries))
        {
            entries.TryRemove(connectionId, out _);
            if (entries.IsEmpty)
                _presence.TryRemove(sessionId, out _);
        }
    }

    // ── DTO helpers ──────────────────────────────────────────────────────────────

    private static string SessionGroup(string sessionId) => $"session:{sessionId}";

    private static object AnnotationToDto(CaptureAnnotation a) => new
    {
        id = a.Id,
        sessionId = a.SessionId,
        captureId = a.CaptureId,
        userId = a.UserId,
        username = a.Username,
        note = a.Note,
        tags = a.Tags,
        createdAt = a.CreatedAt,
        updatedAt = a.UpdatedAt
    };

    private static object ActivityToDto(SessionActivity a) => new
    {
        id = a.Id,
        sessionId = a.SessionId,
        userId = a.UserId,
        username = a.Username,
        action = a.Action,
        details = a.Details,
        timestamp = a.Timestamp
    };

    private record PresenceEntry(string UserId, string Username, DateTimeOffset JoinedAt);
}

/// <summary>
/// Broadcasts collaboration events (annotations, activities) to session groups from controllers.
/// </summary>
public class CollaborationPublisher(IHubContext<CollaborationHub> hub)
{
    public async Task PublishActivityAsync(SessionActivity activity, CancellationToken ct = default)
    {
        var dto = new
        {
            id = activity.Id,
            sessionId = activity.SessionId,
            userId = activity.UserId,
            username = activity.Username,
            action = activity.Action,
            details = activity.Details,
            timestamp = activity.Timestamp
        };
        await hub.Clients.Group($"session:{activity.SessionId}")
            .SendAsync("SessionActivity", dto, ct);
    }
}
