using System.Text.Json;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IoTSpy.Scanner.Reports;

/// <summary>
/// Loads and aggregates everything a report needs from the repositories, for either a
/// device-scoped or a session-scoped report. Kept separate from <see cref="ReportService"/>
/// so the HTML/PDF renderers share one code path for "what goes in the report."
/// </summary>
internal static class ReportDataLoader
{
    // A report is an overview, not an export — cap how many rows of high-volume data render.
    private const int MaxCapturesPerReport = 200;

    private static readonly ScanFindingSeverity[] SeverityOrder =
    [
        ScanFindingSeverity.Critical,
        ScanFindingSeverity.High,
        ScanFindingSeverity.Medium,
        ScanFindingSeverity.Low,
        ScanFindingSeverity.Info
    ];

    public static async Task<ReportData> LoadForDeviceAsync(
        IServiceScopeFactory scopeFactory, Guid deviceId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var scanJobs = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
        var captures = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();
        var protocolMessages = scope.ServiceProvider.GetRequiredService<IProtocolMessageRepository>();

        var device = await devices.GetByIdAsync(deviceId, ct);
        var jobs = await scanJobs.GetByDeviceIdAsync(deviceId, ct);
        var findings = await LoadFindingsAsync(scanJobs, jobs, ct);

        var deviceCaptures = await captures.GetPagedAsync(
            new CaptureFilter(DeviceId: deviceId), page: 1, pageSize: MaxCapturesPerReport, ct);

        var messages = await protocolMessages.GetByDeviceIdAsync(deviceId, ct: ct);

        var deviceName = device is not null ? $"{device.Label} ({device.IpAddress})" : "Unknown Device";

        return new ReportData
        {
            ScopeLabel = $"Device: {deviceName}",
            Device = device,
            ScanJobs = jobs,
            FindingsBySeverity = GroupBySeverity(findings),
            Captures = deviceCaptures.Select(ToSummary).ToList(),
            ProtocolMessages = messages
        };
    }

    public static async Task<ReportData> LoadForSessionAsync(
        IServiceScopeFactory scopeFactory, Guid sessionId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IInvestigationSessionRepository>();
        var scanJobs = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
        var protocolMessages = scope.ServiceProvider.GetRequiredService<IProtocolMessageRepository>();
        var annotations = scope.ServiceProvider.GetRequiredService<ICaptureAnnotationRepository>();
        var activities = scope.ServiceProvider.GetRequiredService<ISessionActivityRepository>();

        var session = await sessions.GetByIdAsync(sessionId, ct);
        var sessionCaptures = await sessions.GetSessionCapturesAsync(sessionId, ct);

        var captures = sessionCaptures
            .Select(sc => sc.Capture)
            .Where(c => c is not null)
            .Cast<CapturedRequest>()
            .OrderByDescending(c => c.Timestamp)
            .Take(MaxCapturesPerReport)
            .ToList();

        var deviceIds = captures.Where(c => c.DeviceId.HasValue).Select(c => c.DeviceId!.Value).Distinct().ToList();

        var jobs = new List<ScanJob>();
        foreach (var deviceId in deviceIds)
            jobs.AddRange(await scanJobs.GetByDeviceIdAsync(deviceId, ct));

        var findings = await LoadFindingsAsync(scanJobs, jobs, ct);
        var messages = deviceIds.Count > 0
            ? await protocolMessages.GetByDeviceIdsAsync(deviceIds, ct: ct)
            : [];

        return new ReportData
        {
            ScopeLabel = $"Session: {session?.Name ?? "Unknown Session"}",
            Session = session,
            ScanJobs = jobs,
            FindingsBySeverity = GroupBySeverity(findings),
            Captures = captures.Select(ToSummary).ToList(),
            Annotations = await annotations.GetBySessionAsync(sessionId, ct),
            ProtocolMessages = messages,
            Activities = await activities.GetBySessionAsync(sessionId, count: 200, ct: ct)
        };
    }

    private static async Task<List<ScanFinding>> LoadFindingsAsync(
        IScanJobRepository scanJobs, List<ScanJob> jobs, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();
        foreach (var job in jobs)
            findings.AddRange(await scanJobs.GetFindingsAsync(job.Id, ct));
        return findings;
    }

    private static List<FindingGroup> GroupBySeverity(List<ScanFinding> findings) =>
        SeverityOrder
            .Select(severity => new FindingGroup(
                severity.ToString(),
                findings.Where(f => f.Severity == severity).ToList()))
            .ToList();

    private static CaptureSummary ToSummary(CapturedRequest c)
    {
        TlsMetadata? tls = null;
        if (!string.IsNullOrEmpty(c.TlsMetadataJson))
        {
            try { tls = JsonSerializer.Deserialize<TlsMetadata>(c.TlsMetadataJson); }
            catch (JsonException) { /* best-effort — malformed metadata just renders blank */ }
        }

        return new CaptureSummary(
            c.Id, c.Method, c.Host, c.Path, c.StatusCode, c.Timestamp, c.DurationMs,
            c.IsTls, c.TlsVersion,
            tls?.SniHostname, tls?.Ja3Hash, tls?.IsLikelyDot ?? false);
    }
}
