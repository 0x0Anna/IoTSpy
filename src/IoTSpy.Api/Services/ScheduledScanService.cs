using Cronos;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Api.Services;

/// <summary>
/// Background service that runs scheduled scans according to their cron expressions
/// and performs drift detection by comparing findings with the previous scan.
///
/// A schedule targets exactly one of: a single <see cref="ScheduledScan.DeviceId"/>,
/// a <see cref="ScheduledScan.TargetCidr"/> block, or a <see cref="ScheduledScan.TargetTag"/>
/// (see <see cref="Core.Utilities.ScheduledScanTargetSelector"/>). CIDR/tag schedules resolve
/// against the full device list at fire time and start one scan job per matching device.
///
/// Every device — whether targeted directly or resolved via CIDR/tag — is gated through the
/// same <see cref="CidrHelper.IsInScope"/> check <c>ScannerController.StartScan</c> uses, so a
/// scheduled scan can never reach a device that a direct, interactive scan would be refused for.
/// Devices outside all active scopes are skipped (and logged), not fatal to the rest of the batch.
/// </summary>
public sealed class ScheduledScanService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScannerService _scannerService;
    private readonly IAlertingService _alertingService;
    private readonly ILogger<ScheduledScanService> _logger;

    public ScheduledScanService(
        IServiceScopeFactory scopeFactory,
        IScannerService scannerService,
        IAlertingService alertingService,
        ILogger<ScheduledScanService> logger)
    {
        _scopeFactory = scopeFactory;
        _scannerService = scannerService;
        _alertingService = alertingService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledScanService started");

        // Compute NextRunAt for all enabled schedules on startup
        await InitializeNextRunAtAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndFireScansAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in scheduled scan loop");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task InitializeNextRunAtAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledScanRepository>();
        var schedules = await repo.GetEnabledAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in schedules)
        {
            if (schedule.NextRunAt is null)
            {
                schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
                await repo.UpdateAsync(schedule, ct);
            }
        }
    }

    private async Task CheckAndFireScansAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledScanRepository>();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var scanJobRepo = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
        var scanScopeRepo = scope.ServiceProvider.GetRequiredService<IScanScopeRepository>();

        var schedules = await repo.GetEnabledAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in schedules)
        {
            if (schedule.NextRunAt is null || schedule.NextRunAt > now)
                continue;

            try
            {
                if (schedule.DeviceId.HasValue)
                {
                    await FireSingleDeviceScheduleAsync(schedule, deviceRepo, scanScopeRepo, scanJobRepo, repo, now, ct);
                }
                else
                {
                    await FireMultiDeviceScheduleAsync(schedule, deviceRepo, scanScopeRepo, scanJobRepo, repo, now, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running scheduled scan {Id}", schedule.Id);
                schedule.LastRunAt = now;
                schedule.LastRunStatus = ScanStatus.Failed;
                schedule.LastRunError = ex.Message;
                schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
                await repo.UpdateAsync(schedule, ct);
            }
        }
    }

    private async Task FireSingleDeviceScheduleAsync(
        ScheduledScan schedule,
        IDeviceRepository deviceRepo,
        IScanScopeRepository scanScopeRepo,
        IScanJobRepository scanJobRepo,
        IScheduledScanRepository repo,
        DateTimeOffset now,
        CancellationToken ct)
    {
        _logger.LogInformation("Firing scheduled scan {Id} for device {DeviceId}", schedule.Id, schedule.DeviceId);

        var device = await deviceRepo.GetByIdAsync(schedule.DeviceId!.Value, ct);
        if (device is null)
        {
            _logger.LogWarning("Device {DeviceId} not found for scheduled scan {Id}", schedule.DeviceId, schedule.Id);
            schedule.LastRunAt = now;
            schedule.LastRunStatus = ScanStatus.Failed;
            schedule.LastRunError = "Device not found";
            schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
            await repo.UpdateAsync(schedule, ct);
            return;
        }

        var activeScopes = await scanScopeRepo.GetActiveAsync(ct);
        if (!CidrHelper.IsInScope(activeScopes, device.IpAddress))
        {
            _logger.LogWarning(
                "Skipping scheduled scan {Id} — device {DeviceId} ({Ip}) is outside all active scan scopes",
                schedule.Id, device.Id, device.IpAddress);
            schedule.LastRunAt = now;
            schedule.LastRunStatus = ScanStatus.Failed;
            schedule.LastRunError = $"Device IP {device.IpAddress} is not within any active scan scope.";
            schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
            await repo.UpdateAsync(schedule, ct);
            return;
        }

        var job = BuildScanJob(device);
        var result = await _scannerService.StartScanAsync(job, ct);
        var previousJobId = schedule.LastScanJobId;

        // StartScanAsync returns immediately (the scan itself runs on a background
        // Task.Run) — wait for a terminal status so LastRunStatus reflects the actual
        // outcome, not just "the scan was kicked off". This also fixes a pre-existing
        // bug where drift detection below ran against an unfinished scan's findings.
        var finalJob = await WaitForTerminalStatusAsync(scanJobRepo, result.Id, ct);

        schedule.LastRunAt = now;
        schedule.LastScanJobId = result.Id;
        schedule.LastRunStatus = finalJob.Status;
        schedule.LastRunError = finalJob.Status == ScanStatus.Failed ? finalJob.ErrorMessage : null;
        schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
        await repo.UpdateAsync(schedule, ct);

        // Drift detection compares this schedule's previous and current job — only
        // meaningful when both jobs targeted the same single device, which is only
        // guaranteed in single-device mode.
        if (previousJobId is not null)
        {
            await DetectDriftAsync(scanJobRepo, previousJobId.Value, result.Id, device, ct);
        }
    }

    private async Task FireMultiDeviceScheduleAsync(
        ScheduledScan schedule,
        IDeviceRepository deviceRepo,
        IScanScopeRepository scanScopeRepo,
        IScanJobRepository scanJobRepo,
        IScheduledScanRepository repo,
        DateTimeOffset now,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Firing scheduled scan {Id} for target list (cidr={Cidr}, tag={Tag})",
            schedule.Id, schedule.TargetCidr, schedule.TargetTag);

        var allDevices = await deviceRepo.GetAllAsync(ct);
        var matched = string.IsNullOrWhiteSpace(schedule.TargetCidr)
            ? allDevices.Where(d => DeviceHasTag(d.Tags, schedule.TargetTag!)).ToList()
            : allDevices.Where(d => CidrHelper.Contains(schedule.TargetCidr!, d.IpAddress)).ToList();

        if (matched.Count == 0)
        {
            _logger.LogWarning("No devices matched target list for scheduled scan {Id}", schedule.Id);
            schedule.LastRunAt = now;
            schedule.LastRunStatus = ScanStatus.Failed;
            schedule.LastRunError = "No matching devices found";
            schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
            await repo.UpdateAsync(schedule, ct);
            return;
        }

        var activeScopes = await scanScopeRepo.GetActiveAsync(ct);
        var inScopeDevices = new List<Device>();
        foreach (var device in matched)
        {
            if (CidrHelper.IsInScope(activeScopes, device.IpAddress))
            {
                inScopeDevices.Add(device);
            }
            else
            {
                _logger.LogWarning(
                    "Skipping device {DeviceId} ({Ip}) for scheduled scan {Id} — outside all active scan scopes",
                    device.Id, device.IpAddress, schedule.Id);
            }
        }

        if (inScopeDevices.Count == 0)
        {
            schedule.LastRunAt = now;
            schedule.LastRunStatus = ScanStatus.Failed;
            schedule.LastRunError = "All matching devices are outside active scan scopes";
            schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
            await repo.UpdateAsync(schedule, ct);
            return;
        }

        // A multi-device schedule has no single job to represent its outcome the way a
        // single-device schedule does. We track the most-recently-started job in
        // LastScanJobId (so the schedule list still links to at least one recent job)
        // and treat the run as Failed overall if ANY device's scan failed — a partial
        // failure is a signal worth surfacing, and there's no per-device status field
        // on ScheduledScan to report it more granularly. Drift detection is skipped
        // entirely here: it compares two ScanJobs' findings, and there's no guarantee
        // the previous representative job and this run's devices overlap.
        Guid? lastJobId = null;
        var failures = new List<string>();

        foreach (var device in inScopeDevices)
        {
            try
            {
                var job = BuildScanJob(device);
                var result = await _scannerService.StartScanAsync(job, ct);
                var finalJob = await WaitForTerminalStatusAsync(scanJobRepo, result.Id, ct);
                lastJobId = result.Id;

                if (finalJob.Status == ScanStatus.Failed)
                    failures.Add($"{device.IpAddress}: {finalJob.ErrorMessage}");
            }
            catch (Exception ex)
            {
                failures.Add($"{device.IpAddress}: {ex.Message}");
                _logger.LogError(ex, "Error running scheduled scan {Id} for device {DeviceId}", schedule.Id, device.Id);
            }
        }

        schedule.LastRunAt = now;
        schedule.LastScanJobId = lastJobId;
        schedule.LastRunStatus = failures.Count == 0 ? ScanStatus.Completed : ScanStatus.Failed;
        schedule.LastRunError = failures.Count == 0 ? null : string.Join("; ", failures);
        schedule.NextRunAt = ComputeNextRun(schedule.CronExpression, now);
        await repo.UpdateAsync(schedule, ct);
    }

    private static ScanJob BuildScanJob(Device device) => new()
    {
        DeviceId = device.Id,
        TargetIp = device.IpAddress,
        PortRange = "1-1024",
        MaxConcurrency = 100,
        TimeoutMs = 3000,
        EnableFingerprinting = true,
        EnableCredentialTest = true,
        EnableCveLookup = true,
        EnableConfigAudit = true
    };

    /// <summary>Case-insensitive membership check against a comma-separated tag string
    /// (same convention as <see cref="CaptureAnnotation.Tags"/>).</summary>
    private static bool DeviceHasTag(string? tags, string tag)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return false;

        return tags
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly ScanStatus[] TerminalStatuses = [ScanStatus.Completed, ScanStatus.Failed, ScanStatus.Cancelled];

    /// <summary>
    /// Polls the scan job until it reaches a terminal status or <see cref="MaxWait"/> elapses.
    /// Checks status before delaying, so a job that is already terminal on the first read
    /// returns immediately with no delay — this keeps unit tests fast.
    /// </summary>
    private async Task<ScanJob> WaitForTerminalStatusAsync(IScanJobRepository scanJobRepo, Guid jobId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + MaxWait;
        ScanJob? job;

        while (true)
        {
            job = await scanJobRepo.GetByIdAsync(jobId, ct);
            if (job is null || TerminalStatuses.Contains(job.Status) || DateTimeOffset.UtcNow >= deadline)
                break;

            await Task.Delay(PollInterval, ct);
        }

        if (job is null)
        {
            _logger.LogWarning("Scan job {JobId} disappeared while waiting for completion", jobId);
            return new ScanJob { Id = jobId, Status = ScanStatus.Failed, ErrorMessage = "Scan job not found after starting" };
        }

        if (!TerminalStatuses.Contains(job.Status))
            _logger.LogWarning("Scan job {JobId} did not reach a terminal status within {MaxWait}", jobId, MaxWait);

        return job;
    }

    private async Task DetectDriftAsync(
        IScanJobRepository scanJobRepo,
        Guid previousJobId,
        Guid newJobId,
        Device device,
        CancellationToken ct)
    {
        try
        {
            var previousFindings = await scanJobRepo.GetFindingsAsync(previousJobId, ct);
            var newFindings = await scanJobRepo.GetFindingsAsync(newJobId, ct);

            var previousCritical = previousFindings.Count(f => f.Severity == ScanFindingSeverity.Critical);
            var newCritical = newFindings.Count(f => f.Severity == ScanFindingSeverity.Critical);

            if (newCritical > previousCritical)
            {
                var delta = newCritical - previousCritical;
                await _alertingService.SendAlertAsync(
                    $"New Critical Findings on {device.Label ?? device.IpAddress}",
                    $"Scheduled scan detected {delta} new Critical finding(s) on device {device.IpAddress}. " +
                    $"Previous: {previousCritical} Critical, Current: {newCritical} Critical.",
                    AlertSeverity.Critical,
                    ct);
            }
            else if (newFindings.Count > previousFindings.Count + 5)
            {
                await _alertingService.SendAlertAsync(
                    $"Finding Drift on {device.Label ?? device.IpAddress}",
                    $"Scheduled scan detected {newFindings.Count - previousFindings.Count} new findings on device {device.IpAddress}.",
                    AlertSeverity.Warning,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drift detection failed for job {NewJobId}", newJobId);
        }
    }

    private DateTimeOffset? ComputeNextRun(string cronExpression, DateTimeOffset from)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression, CronFormat.Standard);
            var next = cron.GetNextOccurrence(from.UtcDateTime, TimeZoneInfo.Utc);
            return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid cron expression '{Cron}' — schedule will not fire", cronExpression);
            return null;
        }
    }
}
