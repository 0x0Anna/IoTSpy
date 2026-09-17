using IoTSpy.Core.Models;

namespace IoTSpy.Scanner.Reports;

/// <summary>
/// Aggregated, template-ready data for a single report render. Populated by
/// <see cref="ReportDataLoader"/> for either a device-scoped or session-scoped report —
/// exactly one of <see cref="Device"/> / <see cref="Session"/> is set.
/// </summary>
internal sealed class ReportData
{
    public required string ScopeLabel { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public Device? Device { get; init; }
    public InvestigationSession? Session { get; init; }

    public List<ScanJob> ScanJobs { get; init; } = [];
    public List<FindingGroup> FindingsBySeverity { get; init; } = [];
    public List<CaptureSummary> Captures { get; init; } = [];
    public List<CaptureAnnotation> Annotations { get; init; } = [];
    public List<PersistedProtocolMessage> ProtocolMessages { get; init; } = [];
    public List<SessionActivity> Activities { get; init; } = [];
}

/// <summary>
/// Findings pre-grouped by severity — grouping is done here rather than in the Scriban
/// template, since Scriban's lambda syntax for filtering is fragile and this is ordinary
/// business logic, not presentation.
/// </summary>
internal sealed record FindingGroup(string Severity, List<ScanFinding> Findings);

/// <summary>
/// Flat, report-friendly projection of a <see cref="CapturedRequest"/> — the raw request/response
/// bodies aren't included (a report is an overview, not a capture export), and TLS metadata is
/// pre-parsed from <see cref="CapturedRequest.TlsMetadataJson"/> since Scriban can't deserialize
/// JSON on the fly.
/// </summary>
internal sealed record CaptureSummary(
    Guid Id,
    string Method,
    string Host,
    string Path,
    int StatusCode,
    DateTimeOffset Timestamp,
    long DurationMs,
    bool IsTls,
    string TlsVersion,
    string? TlsSni,
    string? TlsJa3Hash,
    bool TlsLikelyDot);
