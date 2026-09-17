using System.Collections.Concurrent;
using System.Threading.Channels;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Scanner;

public class ScannerService : IScannerService
{
    /// <summary>
    /// Conservative default: each scan job can itself run up to
    /// <see cref="PortScanner"/>'s per-job MaxConcurrency (up to 100) concurrent TCP probes,
    /// so an unbounded number of concurrent jobs is a resource-exhaustion risk. 5 concurrent
    /// jobs caps worst case outbound connections at 500 while still letting several devices
    /// be scanned in parallel. Overridable via Scanner:MaxConcurrentScans.
    /// </summary>
    public const int DefaultMaxConcurrentScans = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PortScanner _portScanner;
    private readonly ServiceFingerprinter _fingerprinter;
    private readonly CredentialTester _credentialTester;
    private readonly CveLookupService _cveLookup;
    private readonly ConfigAuditor _configAuditor;
    private readonly ILogger<ScannerService> _logger;

    // Populated immediately in StartScanAsync (covers both queued-but-not-yet-admitted and
    // actively-running jobs), removed once the job reaches a terminal state or is cancelled.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _scans = new();

    // Jobs that are enqueued but have not yet been picked up by a worker loop. Used so a
    // cancel of a still-queued job can make the worker loop skip it entirely instead of
    // executing it.
    private readonly ConcurrentDictionary<Guid, byte> _queuedJobIds = new();

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    public ScannerService(
        IServiceScopeFactory scopeFactory,
        PortScanner portScanner,
        ServiceFingerprinter fingerprinter,
        CredentialTester credentialTester,
        CveLookupService cveLookup,
        ConfigAuditor configAuditor,
        ILogger<ScannerService> logger,
        int maxConcurrentScans = DefaultMaxConcurrentScans)
    {
        _scopeFactory = scopeFactory;
        _portScanner = portScanner;
        _fingerprinter = fingerprinter;
        _credentialTester = credentialTester;
        _cveLookup = cveLookup;
        _configAuditor = configAuditor;
        _logger = logger;

        var workerCount = maxConcurrentScans > 0 ? maxConcurrentScans : DefaultMaxConcurrentScans;

        // N worker loops, each processing one job at a time from the shared queue, bounds
        // concurrent scan execution to `workerCount` regardless of how many jobs are queued.
        for (var i = 0; i < workerCount; i++)
            _ = RunWorkerLoopAsync();
    }

    public async Task<ScanJob> StartScanAsync(ScanJob job, CancellationToken ct = default)
    {
        // Persist the job (Status defaults to Pending until a worker admits it)
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
            await repo.AddAsync(job, ct);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scans[job.Id] = cts;
        _queuedJobIds[job.Id] = 0;

        // Channel is unbounded, so this never blocks the caller waiting for a free slot.
        await _queue.Writer.WriteAsync(job.Id, ct);

        return job;
    }

    public async Task CancelScanAsync(Guid scanJobId)
    {
        if (_queuedJobIds.TryRemove(scanJobId, out _))
        {
            // Still queued — remove from tracking so the worker loop skips it when it
            // eventually dequeues the id, and mark the job cancelled directly since
            // ExecuteScanAsync will never run for it.
            if (_scans.TryRemove(scanJobId, out var queuedCts))
            {
                queuedCts.Cancel();
                queuedCts.Dispose();
            }

            await SetJobStatusAsync(scanJobId, ScanStatus.Cancelled);
            return;
        }

        if (_scans.TryRemove(scanJobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public bool IsScanRunning(Guid scanJobId) =>
        _scans.ContainsKey(scanJobId);

    private async Task RunWorkerLoopAsync()
    {
        await foreach (var jobId in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (!_queuedJobIds.TryRemove(jobId, out _))
                    continue; // cancelled while queued

                if (!_scans.TryGetValue(jobId, out var cts))
                    continue; // cancelled and fully removed already

                await ExecuteScanAsync(jobId, cts.Token);
            }
            catch (Exception ex)
            {
                // ExecuteScanAsync already handles its own exceptions; this is a last-resort
                // guard so a single bad job can never take down a worker loop permanently.
                _logger.LogError(ex, "Unexpected error processing queued scan {JobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Runs the actual port-scanning step. Extracted as a seam so tests can substitute a
    /// controllable delay without touching <see cref="PortScanner"/> itself.
    /// </summary>
    protected virtual Task<List<ScanFinding>> ScanPortsAsync(ScanJob job, CancellationToken ct) =>
        _portScanner.ScanAsync(job.TargetIp, job.PortRange, job.MaxConcurrency, job.TimeoutMs, ct);

    private async Task ExecuteScanAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
            var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();

            var job = await repo.GetByIdAsync(jobId, ct);
            if (job is null) return;

            job.Status = ScanStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await repo.UpdateAsync(job, ct);

            _logger.LogInformation("Starting scan {JobId} on {Target}", jobId, job.TargetIp);

            // 3.1 — Port scan
            var openPorts = await ScanPortsAsync(job, ct);

            foreach (var finding in openPorts)
                finding.ScanJobId = jobId;
            await repo.AddFindingsAsync(openPorts, ct);

            // 3.2 — Service fingerprinting
            List<ScanFinding> fingerprints = [];
            if (job.EnableFingerprinting && openPorts.Count > 0)
            {
                fingerprints = await _fingerprinter.FingerprintAsync(
                    job.TargetIp, openPorts, job.TimeoutMs, ct);

                foreach (var finding in fingerprints)
                    finding.ScanJobId = jobId;
                await repo.AddFindingsAsync(fingerprints, ct);
            }

            // 3.3 — Default credential testing
            if (job.EnableCredentialTest && openPorts.Count > 0)
            {
                var credFindings = await _credentialTester.TestAsync(
                    job.TargetIp, openPorts, job.TimeoutMs, ct);

                foreach (var finding in credFindings)
                    finding.ScanJobId = jobId;
                await repo.AddFindingsAsync(credFindings, ct);
            }

            // 3.4 — CVE lookup
            if (job.EnableCveLookup && fingerprints.Count > 0)
            {
                var cveFindings = await _cveLookup.LookupAsync(fingerprints, ct);
                foreach (var finding in cveFindings)
                    finding.ScanJobId = jobId;
                await repo.AddFindingsAsync(cveFindings, ct);
            }

            // 3.5 — Config audit
            if (job.EnableConfigAudit && openPorts.Count > 0)
            {
                var configFindings = await _configAuditor.AuditAsync(
                    job.TargetIp, openPorts, job.TimeoutMs, ct);

                foreach (var finding in configFindings)
                    finding.ScanJobId = jobId;
                await repo.AddFindingsAsync(configFindings, ct);
            }

            // Complete the job
            job = await repo.GetByIdAsync(jobId, ct);
            if (job is null) return;

            var allFindings = await repo.GetFindingsAsync(jobId, ct);
            job.TotalFindings = allFindings.Count;
            job.Status = ScanStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await repo.UpdateAsync(job, ct);

            // Update device security score
            await UpdateSecurityScoreAsync(job, allFindings, deviceRepo, ct);

            _logger.LogInformation("Scan {JobId} completed: {Count} findings", jobId, job.TotalFindings);
        }
        catch (OperationCanceledException)
        {
            await SetJobStatusAsync(jobId, ScanStatus.Cancelled);
            _logger.LogInformation("Scan {JobId} was cancelled", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan {JobId} failed", jobId);
            await SetJobStatusAsync(jobId, ScanStatus.Failed, ex.Message);
        }
        finally
        {
            if (_scans.TryRemove(jobId, out var cts))
                cts.Dispose();
        }
    }

    private async Task SetJobStatusAsync(Guid jobId, ScanStatus status, string? error = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScanJobRepository>();
            var job = await repo.GetByIdAsync(jobId);
            if (job is null) return;

            job.Status = status;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ErrorMessage = error;
            await repo.UpdateAsync(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update scan job {JobId} status to {Status}", jobId, status);
        }
    }

    private static async Task UpdateSecurityScoreAsync(
        ScanJob job, List<ScanFinding> findings, IDeviceRepository deviceRepo, CancellationToken ct)
    {
        var device = await deviceRepo.GetByIdAsync(job.DeviceId, ct);
        if (device is null) return;

        // Score: start at 100, deduct per finding severity
        var score = 100;
        foreach (var finding in findings)
        {
            score -= finding.Severity switch
            {
                ScanFindingSeverity.Critical => 25,
                ScanFindingSeverity.High => 15,
                ScanFindingSeverity.Medium => 10,
                ScanFindingSeverity.Low => 5,
                _ => 0
            };
        }

        device.SecurityScore = Math.Max(0, score);
        await deviceRepo.UpdateAsync(device, ct);
    }
}
