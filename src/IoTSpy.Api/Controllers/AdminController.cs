using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize(Roles = "admin")]
[Route("api/admin")]
public class AdminController(
    IoTSpyDbContext db,
    IAuditRepository auditRepo,
    DataRetentionSettingsService retentionSettings) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var captureCount = await db.Captures.CountAsync(ct);
        var packetCount = await db.Packets.CountAsync(ct);
        var scanFindingCount = await db.ScanFindings.CountAsync(ct);
        var oldestCapture = await db.Captures.AnyAsync(ct)
            ? await db.Captures.MinAsync(c => (DateTimeOffset?)c.Timestamp, ct)
            : null;
        var oldestPacket = await db.Packets.AnyAsync(ct)
            ? await db.Packets.MinAsync(p => (DateTimeOffset?)p.Timestamp, ct)
            : null;
        var auditStats = await auditRepo.GetStatsAsync(ct);

        return Ok(new
        {
            captures = new
            {
                count = captureCount,
                estimatedSizeBytes = captureCount * 2048L,
                oldestTimestamp = oldestCapture
            },
            packets = new
            {
                count = packetCount,
                estimatedSizeBytes = packetCount * 512L,
                oldestTimestamp = oldestPacket
            },
            scanFindings = new { count = scanFindingCount },
            auditLog = new
            {
                count = auditStats.MainCount,
                archiveCount = auditStats.ArchiveCount,
                oldestTimestamp = auditStats.OldestMainTimestamp,
                oldestArchiveTimestamp = auditStats.OldestArchiveTimestamp
            }
        });
    }

    [HttpPost("audit/archive")]
    public async Task<IActionResult> ArchiveAuditLog(
        [FromQuery] int olderThanDays = 90, CancellationToken ct = default)
    {
        if (olderThanDays < 1)
            return BadRequest(new { error = "olderThanDays must be >= 1" });

        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var archived = await auditRepo.ArchiveOlderThanAsync(cutoff, ct);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "ArchiveAuditLog",
            EntityType = "AuditEntry",
            Details = $"Archived {archived} audit entries older than {olderThanDays} days",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { archived });
    }

    [HttpDelete("audit/archive")]
    public async Task<IActionResult> PurgeAuditArchive(
        [FromQuery] int olderThanDays = 365, CancellationToken ct = default)
    {
        if (olderThanDays < 1)
            return BadRequest(new { error = "olderThanDays must be >= 1" });

        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var purged = await auditRepo.PurgeArchiveOlderThanAsync(cutoff, ct);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "PurgeAuditArchive",
            EntityType = "AuditArchiveEntry",
            Details = $"Purged {purged} archive entries older than {olderThanDays} days",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { purged });
    }

    [HttpDelete("captures")]
    public async Task<IActionResult> PurgeCaptures(
        [FromQuery] int? olderThanDays,
        [FromQuery] Guid? deviceId,
        [FromQuery] string? host,
        [FromQuery] bool purgeAll = false,
        CancellationToken ct = default)
    {
        if (!purgeAll && !olderThanDays.HasValue && !deviceId.HasValue && string.IsNullOrEmpty(host))
            return BadRequest(new { error = "Specify at least one filter, or use purgeAll=true" });

        var query = db.Captures.AsQueryable();
        if (!purgeAll)
        {
            if (olderThanDays.HasValue)
                query = query.Where(c => c.Timestamp < DateTimeOffset.UtcNow.AddDays(-olderThanDays.Value));
            if (deviceId.HasValue)
                query = query.Where(c => c.DeviceId == deviceId);
            if (!string.IsNullOrEmpty(host))
                query = query.Where(c => c.Host == host);
        }

        // ExecuteDeleteAsync issues a single SQL DELETE without hydrating rows
        // into the change tracker — safe for tables with hundreds of thousands
        // of rows that the previous ToListAsync()+RemoveRange() pattern OOMed on.
        var deleted = await query.ExecuteDeleteAsync(ct);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "PurgeCaptures",
            EntityType = "CapturedRequest",
            Details = $"Purged {deleted} captures",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { deleted });
    }

    [HttpDelete("packets")]
    public async Task<IActionResult> PurgePackets(
        [FromQuery] int? olderThanDays,
        [FromQuery] bool purgeAll = false,
        CancellationToken ct = default)
    {
        if (!purgeAll && !olderThanDays.HasValue)
            return BadRequest(new { error = "Specify olderThanDays, or use purgeAll=true" });

        var query = db.Packets.AsQueryable();
        if (!purgeAll && olderThanDays.HasValue)
            query = query.Where(p => p.Timestamp < DateTimeOffset.UtcNow.AddDays(-olderThanDays.Value));

        var deleted = await query.ExecuteDeleteAsync(ct);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "PurgePackets",
            EntityType = "CapturedPacket",
            Details = $"Purged {deleted} packets",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { deleted });
    }

    [HttpGet("export/logs")]
    public async Task<IActionResult> ExportLogs([FromQuery] string format = "json", CancellationToken ct = default)
    {
        var captures = await db.Captures.AsNoTracking()
            .Include(c => c.Device)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (format == "csv")
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Timestamp,Method,Host,Path,StatusCode,RequestSize,ResponseSize,Device");
            foreach (var c in captures)
                csv.AppendLine($"{c.Timestamp:O},{Csv(c.Method)},{Csv(c.Host)},{Csv(c.Path)},{c.StatusCode},{c.RequestBodySize},{c.ResponseBodySize},{Csv(c.Device?.Label ?? c.Device?.Hostname ?? "")}");
            return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "captures.csv");
        }

        var json = System.Text.Json.JsonSerializer.Serialize(captures,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "captures.json");
    }

    [HttpGet("export/packets")]
    public async Task<IActionResult> ExportPackets([FromQuery] string format = "json", CancellationToken ct = default)
    {
        var packets = await db.Packets.AsNoTracking()
            .OrderBy(p => p.Timestamp)
            .ToListAsync(ct);

        if (format == "csv")
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Timestamp,Protocol,SourceIp,DestinationIp,SourcePort,DestinationPort,Length");
            foreach (var p in packets)
                csv.AppendLine($"{p.Timestamp:O},{Csv(p.Protocol)},{Csv(p.SourceIp)},{Csv(p.DestinationIp)},{p.SourcePort},{p.DestinationPort},{p.Length}");
            return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "packets.csv");
        }

        var json = System.Text.Json.JsonSerializer.Serialize(packets,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "packets.json");
    }

    [HttpGet("export/config")]
    public async Task<IActionResult> ExportConfig(CancellationToken ct = default)
    {
        var config = new
        {
            manipulationRules = await db.ManipulationRules.AsNoTracking().ToListAsync(ct),
            breakpoints = await db.Breakpoints.AsNoTracking().ToListAsync(ct),
            fuzzerJobs = await db.FuzzerJobs.AsNoTracking().ToListAsync(ct),
            scheduledScans = await db.ScheduledScans.AsNoTracking().ToListAsync(ct),
            openRtbPolicies = await db.OpenRtbPiiPolicies.AsNoTracking().ToListAsync(ct),
            apiSpecDocuments = await db.ApiSpecDocuments.AsNoTracking()
                .Include(d => d.ReplacementRules)
                .ToListAsync(ct),
            exportedAt = DateTimeOffset.UtcNow
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "iotspy-config.json");
    }

    [HttpGet("retention")]
    public IActionResult GetRetentionSettings()
    {
        var opts = retentionSettings.Current;
        return Ok(new
        {
            enabled = opts.Enabled,
            captureRetentionDays = opts.CaptureRetentionDays,
            packetRetentionDays = opts.PacketRetentionDays,
            scanJobRetentionDays = opts.ScanJobRetentionDays,
            openRtbEventRetentionDays = opts.OpenRtbEventRetentionDays,
            auditRetentionDays = opts.AuditRetentionDays,
            auditArchivePurgeDays = opts.AuditArchivePurgeDays,
            runIntervalHours = opts.RunIntervalHours,
        });
    }

    public record UpdateRetentionRequest(
        bool Enabled,
        int CaptureRetentionDays,
        int PacketRetentionDays,
        int ScanJobRetentionDays,
        int OpenRtbEventRetentionDays,
        int AuditRetentionDays,
        int AuditArchivePurgeDays,
        double RunIntervalHours);

    [HttpPut("retention")]
    public async Task<IActionResult> UpdateRetentionSettings(
        [FromBody] UpdateRetentionRequest request, CancellationToken ct)
    {
        if (request.RunIntervalHours <= 0)
            return BadRequest(new { error = "runIntervalHours must be > 0" });
        if (request.CaptureRetentionDays < 0 || request.PacketRetentionDays < 0 ||
            request.ScanJobRetentionDays < 0 || request.OpenRtbEventRetentionDays < 0 ||
            request.AuditRetentionDays < 0 || request.AuditArchivePurgeDays < 0)
            return BadRequest(new { error = "Retention days must be >= 0 (0 = never purge)" });

        var opts = new DataRetentionOptions
        {
            Enabled = request.Enabled,
            CaptureRetentionDays = request.CaptureRetentionDays,
            PacketRetentionDays = request.PacketRetentionDays,
            ScanJobRetentionDays = request.ScanJobRetentionDays,
            OpenRtbEventRetentionDays = request.OpenRtbEventRetentionDays,
            AuditRetentionDays = request.AuditRetentionDays,
            AuditArchivePurgeDays = request.AuditArchivePurgeDays,
            RunIntervalHours = request.RunIntervalHours,
        };
        retentionSettings.Update(opts);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "UpdateRetentionSettings",
            EntityType = "DataRetentionOptions",
            Details = $"Enabled={opts.Enabled}, CaptureDays={opts.CaptureRetentionDays}, PacketDays={opts.PacketRetentionDays}, ScanDays={opts.ScanJobRetentionDays}, OpenRtbDays={opts.OpenRtbEventRetentionDays}, AuditDays={opts.AuditRetentionDays}, AuditPurgeDays={opts.AuditArchivePurgeDays}, IntervalHours={opts.RunIntervalHours}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { message = "Retention settings updated. Changes take effect on the next scheduled pass." });
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Neutralize formula injection (CSV/DDE) for values that could be interpreted
        // as formulas by Excel/Sheets when opened, e.g. a captured Host or Path
        // starting with '=', '+', '-', '@', tab, or CR.
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            value = "'" + value;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
