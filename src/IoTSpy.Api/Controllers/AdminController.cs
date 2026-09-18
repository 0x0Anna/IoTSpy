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
        var databaseSizeBytes = await GetDatabaseSizeBytesAsync(db, ct);

        return Ok(new
        {
            captures = new
            {
                count = captureCount,
                oldestTimestamp = oldestCapture
            },
            packets = new
            {
                count = packetCount,
                oldestTimestamp = oldestPacket
            },
            scanFindings = new { count = scanFindingCount },
            auditLog = new
            {
                count = auditStats.MainCount,
                archiveCount = auditStats.ArchiveCount,
                oldestTimestamp = auditStats.OldestMainTimestamp,
                oldestArchiveTimestamp = auditStats.OldestArchiveTimestamp
            },
            database = new
            {
                estimatedSizeBytes = databaseSizeBytes
            }
        });
    }

    /// <summary>
    /// Real, provider-correct whole-database size — replaces the previous
    /// per-entity magic-number estimates (count * 2048 / count * 512), which
    /// bore no relation to actual storage. Per-table size isn't cheaply
    /// available on SQLite without the optional dbstat virtual table, so we
    /// report one accurate whole-database figure instead of a fabricated
    /// per-table breakdown.
    /// </summary>
    private static async Task<long?> GetDatabaseSizeBytesAsync(IoTSpyDbContext db, CancellationToken ct)
    {
        var provider = db.Database.ProviderName;
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var pageCount = await ExecuteScalarAsync(db, "PRAGMA page_count;", ct);
            var pageSize = await ExecuteScalarAsync(db, "PRAGMA page_size;", ct);
            return pageCount * pageSize;
        }

        if (provider == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return await ExecuteScalarAsync(db, "SELECT pg_database_size(current_database());", ct);
        }

        return null;
    }

    /// <summary>
    /// Raw ADO scalar execution — PRAGMA statements aren't composable SQL, so EF's
    /// SqlQueryRaw&lt;T&gt;().SingleAsync()/.ToListAsync() (which wrap the SQL in an
    /// outer SELECT) throw. Going straight through the connection avoids that.
    /// </summary>
    private static async Task<long> ExecuteScalarAsync(IoTSpyDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
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
            // Standalone rules only — spec-attached rules are already nested under apiSpecDocuments above.
            contentReplacementRules = await db.ContentReplacementRules.AsNoTracking()
                .Where(r => r.ApiSpecDocumentId == null)
                .ToListAsync(ct),
            protoSchemas = await db.ProtoSchemas.AsNoTracking().ToListAsync(ct),
            exportedAt = DateTimeOffset.UtcNow
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "iotspy-config.json");
    }

    /// <summary>
    /// Imports the parts of the <see cref="ExportConfig"/> bundle that
    /// <c>POST /api/manipulation/import</c> does not already own: scheduled
    /// scans, fuzzer jobs, OpenRTB PII policies, standalone content
    /// replacement rules, and gRPC proto schemas. Manipulation rules,
    /// breakpoints, and API spec documents (with their attached replacement
    /// rules) round-trip through <c>/api/manipulation/import</c> instead —
    /// the split mirrors where each entity type is already owned/edited.
    /// Every imported entity gets a freshly generated Id to avoid colliding
    /// with existing rows, matching the pattern used by
    /// <see cref="ManipulationController.ImportRuleset"/>.
    /// </summary>
    [HttpPost("import/config")]
    public async Task<IActionResult> ImportConfig([FromBody] ImportConfigDto dto, CancellationToken ct)
    {
        int scheduledScansImported = 0, fuzzerJobsImported = 0, openRtbPoliciesImported = 0,
            contentRulesImported = 0, contentRulesSkipped = 0, protoSchemasImported = 0;

        foreach (var scan in dto.ScheduledScans ?? [])
        {
            scan.Id = Guid.NewGuid();
            scan.LastRunAt = null;
            scan.LastScanJobId = null;
            scan.LastRunStatus = null;
            scan.LastRunError = null;
            scan.NextRunAt = null;
            db.ScheduledScans.Add(scan);
            scheduledScansImported++;
        }

        foreach (var job in dto.FuzzerJobs ?? [])
        {
            job.Id = Guid.NewGuid();
            db.FuzzerJobs.Add(job);
            fuzzerJobsImported++;
        }

        foreach (var policy in dto.OpenRtbPolicies ?? [])
        {
            policy.Id = Guid.NewGuid();
            db.OpenRtbPiiPolicies.Add(policy);
            openRtbPoliciesImported++;
        }

        foreach (var rule in dto.ContentReplacementRules ?? [])
        {
            if (string.IsNullOrEmpty(rule.Host))
            {
                contentRulesSkipped++;
                continue;
            }
            rule.Id = Guid.NewGuid();
            rule.ApiSpecDocumentId = null;
            rule.ApiSpecDocument = null;
            db.ContentReplacementRules.Add(rule);
            contentRulesImported++;
        }

        foreach (var schema in dto.ProtoSchemas ?? [])
        {
            schema.Id = Guid.NewGuid();
            db.ProtoSchemas.Add(schema);
            protoSchemasImported++;
        }

        await db.SaveChangesAsync(ct);

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "ImportConfig",
            EntityType = "ConfigBundle",
            Details = $"Imported {scheduledScansImported} scheduled scans, {fuzzerJobsImported} fuzzer jobs, " +
                      $"{openRtbPoliciesImported} OpenRTB policies, {contentRulesImported} content rules " +
                      $"({contentRulesSkipped} skipped for missing Host), {protoSchemasImported} proto schemas",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new
        {
            scheduledScansImported,
            fuzzerJobsImported,
            openRtbPoliciesImported,
            contentRulesImported,
            contentRulesSkipped,
            protoSchemasImported
        });
    }

    // ── Backup / restore (SQLite only) ──────────────────────────────────────

    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private static readonly byte[] SqliteHeaderMagic = "SQLite format 3\0"u8.ToArray();

    [HttpGet("backup")]
    public async Task<IActionResult> BackupDatabase(CancellationToken ct)
    {
        if (db.Database.ProviderName != SqliteProvider)
            return StatusCode(501, new { error = "Backup is only supported for SQLite in this version. For Postgres, use pg_dump directly." });

        var tempPath = Path.Combine(Path.GetTempPath(), $"iotspy-backup-{Guid.NewGuid():N}.db");
        try
        {
            await VacuumIntoAsync(db, tempPath, ct);
            var bytes = await System.IO.File.ReadAllBytesAsync(tempPath, ct);

            await auditRepo.AddAsync(new AuditEntry
            {
                Username = User.Identity?.Name ?? "system",
                Action = "DatabaseBackup",
                EntityType = "Database",
                Details = $"Backup created ({bytes.LongLength} bytes)",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
            }, ct);

            return File(bytes, "application/octet-stream", $"iotspy-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db");
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    [HttpPost("restore")]
    [RequestSizeLimit(1024L * 1024 * 1024)] // 1 GiB — generous cap for a full DB snapshot upload
    public async Task<IActionResult> RestoreDatabase(IFormFile file, CancellationToken ct)
    {
        if (db.Database.ProviderName != SqliteProvider)
            return StatusCode(501, new { error = "Restore is only supported for SQLite in this version. For Postgres, use pg_restore directly." });

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        var dbPath = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(dbPath))
            return StatusCode(500, new { error = "Could not resolve the live database file path" });

        var dbDirectory = Path.GetDirectoryName(dbPath);
        var preRestoreBackupName = $"iotspy.pre-restore-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.db";
        var preRestoreBackupPath = Path.Combine(string.IsNullOrEmpty(dbDirectory) ? "." : dbDirectory, preRestoreBackupName);

        await using (var uploadStream = file.OpenReadStream())
        {
            var header = new byte[SqliteHeaderMagic.Length];
            var read = await uploadStream.ReadAsync(header.AsMemory(0, header.Length), ct);
            if (read < header.Length || !header.AsSpan().SequenceEqual(SqliteHeaderMagic))
                return BadRequest(new { error = "Uploaded file is not a valid SQLite database" });

            await VacuumIntoAsync(db, preRestoreBackupPath, ct);

            // Scoped to this connection's pool only — ClearAllPools() is process-wide and
            // would tear down every other SQLite connection in the process (e.g. concurrently
            // running in-memory-mode test fixtures elsewhere), not just this one.
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection());

            uploadStream.Seek(0, SeekOrigin.Begin);
            await using var destination = System.IO.File.Create(dbPath);
            await uploadStream.CopyToAsync(destination, ct);
        }

        await auditRepo.AddAsync(new AuditEntry
        {
            Username = User.Identity?.Name ?? "system",
            Action = "DatabaseRestore",
            EntityType = "Database",
            Details = $"Database restored from upload ({file.Length} bytes). Pre-restore backup saved as {preRestoreBackupName}.",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        }, ct);

        return Ok(new { message = $"Database restored. Pre-restore backup saved as {preRestoreBackupName}." });
    }

    private static async Task VacuumIntoAsync(IoTSpyDbContext db, string destinationPath, CancellationToken ct)
    {
        // Server-generated path, never built from request input — no injection concern
        // in inlining it directly into the VACUUM INTO statement.
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}';";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
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
            protocolMessageRetentionDays = opts.ProtocolMessageRetentionDays,
            hostBaselineRetentionDays = opts.HostBaselineRetentionDays,
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
        double RunIntervalHours,
        int ProtocolMessageRetentionDays = 14,
        int HostBaselineRetentionDays = 30);

    [HttpPut("retention")]
    public async Task<IActionResult> UpdateRetentionSettings(
        [FromBody] UpdateRetentionRequest request, CancellationToken ct)
    {
        if (request.RunIntervalHours <= 0)
            return BadRequest(new { error = "runIntervalHours must be > 0" });
        if (request.CaptureRetentionDays < 0 || request.PacketRetentionDays < 0 ||
            request.ScanJobRetentionDays < 0 || request.OpenRtbEventRetentionDays < 0 ||
            request.AuditRetentionDays < 0 || request.AuditArchivePurgeDays < 0 ||
            request.ProtocolMessageRetentionDays < 0 || request.HostBaselineRetentionDays < 0)
            return BadRequest(new { error = "Retention days must be >= 0 (0 = never purge)" });

        var opts = new DataRetentionOptions
        {
            Enabled = request.Enabled,
            CaptureRetentionDays = request.CaptureRetentionDays,
            PacketRetentionDays = request.PacketRetentionDays,
            ScanJobRetentionDays = request.ScanJobRetentionDays,
            OpenRtbEventRetentionDays = request.OpenRtbEventRetentionDays,
            ProtocolMessageRetentionDays = request.ProtocolMessageRetentionDays,
            HostBaselineRetentionDays = request.HostBaselineRetentionDays,
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
            Details = $"Enabled={opts.Enabled}, CaptureDays={opts.CaptureRetentionDays}, PacketDays={opts.PacketRetentionDays}, ScanDays={opts.ScanJobRetentionDays}, OpenRtbDays={opts.OpenRtbEventRetentionDays}, ProtocolMessageDays={opts.ProtocolMessageRetentionDays}, HostBaselineDays={opts.HostBaselineRetentionDays}, AuditDays={opts.AuditRetentionDays}, AuditPurgeDays={opts.AuditArchivePurgeDays}, IntervalHours={opts.RunIntervalHours}",
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

public record ImportConfigDto(
    List<ScheduledScan>? ScheduledScans = null,
    List<FuzzerJob>? FuzzerJobs = null,
    List<OpenRtbPiiPolicy>? OpenRtbPolicies = null,
    List<ContentReplacementRule>? ContentReplacementRules = null,
    List<ProtoSchema>? ProtoSchemas = null);
