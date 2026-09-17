using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/scanner")]
public class ScannerController(
    IScannerService scanner,
    IScanJobRepository scanJobs,
    IDeviceRepository devices,
    IScanScopeRepository scanScopes,
    IAuditRepository audit) : ControllerBase
{
    // Bounds for the dual-use scanner. Aggressive defaults are unfriendly on
    // shared networks; the previous MaxConcurrency=100 default could SYN-flood
    // a small subnet. PortRange string length is bounded to prevent quadratic
    // parser work on pathological inputs (the parser itself also caps the
    // resolved port count).
    private const int MaxPortRangeStringLength = 256;
    private const int DefaultMaxConcurrency = 25;
    private const int MaxAllowedConcurrency = 100;

    private Guid? CurrentUserId => Guid.TryParse(
        HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string CurrentUsername => HttpContext.User.Identity?.Name ?? "system";
    private string CurrentIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    [HttpPost("scan")]
    public async Task<IActionResult> StartScan([FromBody] StartScanDto dto, CancellationToken ct = default)
    {
        if (!dto.ConsentAcknowledged)
            return BadRequest("You must acknowledge that you have authorisation to scan this device.");

        var device = await devices.GetByIdAsync(dto.DeviceId);
        if (device is null) return NotFound("Device not found");

        var activeScopes = await scanScopes.GetActiveAsync(ct);
        if (activeScopes.Count > 0)
        {
            var inScope = activeScopes.Any(s => IoTSpy.Scanner.CidrHelper.Contains(s.Cidr, device.IpAddress));
            if (!inScope)
                return StatusCode(403, $"Device IP {device.IpAddress} is not within any active scan scope.");
        }

        var portRange = dto.PortRange ?? "1-1024";
        if (portRange.Length > MaxPortRangeStringLength)
            return BadRequest($"PortRange exceeds {MaxPortRangeStringLength} characters.");

        var requestedConcurrency = dto.MaxConcurrency ?? DefaultMaxConcurrency;
        var clampedConcurrency = Math.Clamp(requestedConcurrency, 1, MaxAllowedConcurrency);

        var job = new ScanJob
        {
            DeviceId = dto.DeviceId,
            TargetIp = device.IpAddress,
            PortRange = portRange,
            MaxConcurrency = clampedConcurrency,
            TimeoutMs = dto.TimeoutMs ?? 3000,
            EnableFingerprinting = dto.EnableFingerprinting ?? true,
            EnableCredentialTest = dto.EnableCredentialTest ?? true,
            EnableCveLookup = dto.EnableCveLookup ?? true,
            EnableConfigAudit = dto.EnableConfigAudit ?? true
        };

        var result = await scanner.StartScanAsync(job);
        return Ok(result);
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> ListJobs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] ScanStatus? status = null,
        [FromQuery] Guid? deviceId = null,
        [FromQuery] DateTimeOffset? createdAfter = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var items = await scanJobs.GetAllAsync(page, pageSize, status, deviceId, createdAfter, ct);
        var total = await scanJobs.CountAsync(status, deviceId, createdAfter, ct);
        return Ok(new { items, total, page, pageSize, pages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> GetJob(Guid id)
    {
        var job = await scanJobs.GetByIdAsync(id);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("jobs/{id:guid}/findings")]
    public async Task<IActionResult> GetFindings(Guid id)
    {
        var job = await scanJobs.GetByIdAsync(id);
        if (job is null) return NotFound();
        return Ok(await scanJobs.GetFindingsAsync(id));
    }

    [HttpGet("device/{deviceId:guid}")]
    public async Task<IActionResult> GetByDevice(Guid deviceId) =>
        Ok(await scanJobs.GetByDeviceIdAsync(deviceId));

    [HttpGet("jobs/{id:guid}/status")]
    public async Task<IActionResult> GetStatus(Guid id)
    {
        var job = await scanJobs.GetByIdAsync(id);
        if (job is null) return NotFound();
        return Ok(new
        {
            job.Id,
            job.Status,
            job.TotalFindings,
            job.StartedAt,
            job.CompletedAt,
            job.ErrorMessage,
            IsRunning = scanner.IsScanRunning(id)
        });
    }

    [HttpPost("jobs/{id:guid}/cancel")]
    public async Task<IActionResult> CancelScan(Guid id)
    {
        var job = await scanJobs.GetByIdAsync(id);
        if (job is null) return NotFound();
        await scanner.CancelScanAsync(id);
        return Ok();
    }

    [HttpDelete("jobs/{id:guid}")]
    public async Task<IActionResult> DeleteJob(Guid id)
    {
        await scanJobs.DeleteAsync(id);
        return NoContent();
    }

    [HttpDelete("jobs/bulk")]
    public async Task<IActionResult> BulkDeleteJobs([FromBody] BulkDeleteJobsDto dto, CancellationToken ct)
    {
        var deleted = await scanJobs.DeleteByFilterAsync(dto.Status, dto.CompletedBefore, ct);
        return Ok(new { deleted });
    }

    [HttpPost("jobs/cancel-all")]
    public async Task<IActionResult> CancelAllScans(CancellationToken ct)
    {
        var allJobs = await scanJobs.GetAllAsync(1, 1000, ct: ct);
        var runningIds = allJobs.Where(j => scanner.IsScanRunning(j.Id)).Select(j => j.Id).ToList();
        foreach (var id in runningIds)
            await scanner.CancelScanAsync(id);
        return Ok(new { cancelled = runningIds.Count });
    }

    [HttpPatch("findings/{id:guid}")]
    public async Task<IActionResult> PatchFinding(Guid id, [FromBody] PatchFindingDto dto, CancellationToken ct)
    {
        var finding = await scanJobs.GetFindingByIdAsync(id, ct);
        if (finding is null) return NotFound();

        var oldScore = finding.CvssScore;
        finding.CvssScore = dto.CvssScore;
        var updated = await scanJobs.UpdateFindingAsync(finding, ct);

        await audit.AddAsync(new AuditEntry
        {
            UserId = CurrentUserId,
            Username = CurrentUsername,
            Action = "FindingCvssOverride",
            EntityType = "ScanFinding",
            EntityId = id.ToString(),
            OldValue = oldScore?.ToString(),
            NewValue = dto.CvssScore?.ToString(),
            IpAddress = CurrentIp
        }, ct);

        return Ok(updated);
    }

    [HttpGet("jobs/{id:guid}/export")]
    public async Task<IActionResult> ExportFindings(Guid id, CancellationToken ct)
    {
        var job = await scanJobs.GetByIdAsync(id, ct);
        if (job is null) return NotFound();
        var findings = await scanJobs.GetFindingsAsync(id, ct);
        var bundle = new { jobId = id, deviceId = job.DeviceId, exportedAt = DateTimeOffset.UtcNow, findings };
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"scan-{id}.json");
    }
}

public record StartScanDto(
    Guid DeviceId,
    string? PortRange = null,
    int? MaxConcurrency = null,
    int? TimeoutMs = null,
    bool? EnableFingerprinting = null,
    bool? EnableCredentialTest = null,
    bool? EnableCveLookup = null,
    bool? EnableConfigAudit = null,
    bool ConsentAcknowledged = false
);

public record BulkDeleteJobsDto(
    ScanStatus? Status = null,
    DateTimeOffset? CompletedBefore = null
);

public record PatchFindingDto(double? CvssScore);
