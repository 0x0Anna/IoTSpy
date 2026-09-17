using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Manipulation;

public class ManipulationService(
    RulesEngine rulesEngine,
    CSharpScriptEngine csharpEngine,
    JavaScriptEngine jsEngine,
    ReplayService replayService,
    FuzzerService fuzzerService,
    IApiSpecService apiSpecService,
    IManipulationRuleCache ruleCache,
    IServiceScopeFactory scopeFactory,
    IAlertingService alerting,
    ILogger<ManipulationService> logger) : IManipulationService
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningFuzzers = new();

    public async Task<bool> ApplyAsync(HttpMessage message, ManipulationPhase phase, CancellationToken ct = default)
    {
        var modified = false;

        using var scope = scopeFactory.CreateScope();

        // 1. Apply declarative rules (served from cache; invalidated on rule mutations)
        var rules = await ruleCache.GetEnabledAsync(ct);
        if (rules.Count > 0)
        {
            var rulesModified = await rulesEngine.ApplyRulesAsync(message, phase, rules, ct);
            if (rulesModified) modified = true;

            // Re-check matches for alert-opted-in rules only (cheap: AlertOnMatch is expected
            // rare/opt-in). Kept separate from ApplyRulesAsync's own matching pass rather than
            // changing that method's return shape, which every existing RulesEngineTests case
            // asserts as a plain bool.
            foreach (var rule in rules.Where(r => r.Phase == phase && r.AlertOnMatch))
            {
                if (!RulesEngine.Matches(rule, message)) continue;
                await SafeAlertAsync($"Rule '{rule.Name}' fired", $"Host: {message.Host}, Path: {message.Path}", ct);
            }
        }

        // 2. Apply API spec content replacement (response phase only)
        if (phase == ManipulationPhase.Response)
        {
            var specModified = await apiSpecService.ApplyMockAsync(message, phase, ct);
            if (specModified) modified = true;
        }

        // 3. Apply scripted breakpoints
        var bpRepo = scope.ServiceProvider.GetRequiredService<IBreakpointRepository>();
        var breakpoints = await bpRepo.GetEnabledAsync(ct);

        foreach (var bp in breakpoints.Where(b => b.Phase == phase))
        {
            if (!MatchesBreakpoint(bp, message))
                continue;

            try
            {
                var bpModified = bp.Language switch
                {
                    ScriptLanguage.CSharp => await csharpEngine.ExecuteAsync(bp.ScriptCode, message, ct),
                    ScriptLanguage.JavaScript => await jsEngine.ExecuteAsync(bp.ScriptCode, message, ct),
                    _ => false
                };

                if (bpModified)
                {
                    modified = true;
                    logger.LogDebug("Breakpoint {Name} ({Language}) modified message for {Host}{Path}",
                        bp.Name, bp.Language, message.Host, message.Path);
                }

                if (bp.AlertOnMatch)
                    await SafeAlertAsync($"Breakpoint '{bp.Name}' fired", $"Host: {message.Host}, Path: {message.Path}", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Breakpoint {Name} execution failed", bp.Name);
            }
        }

        if (modified)
            message.WasModified = true;

        return modified;
    }

    // Alert delivery failures must never break the proxy pipeline — isolate them the
    // same way breakpoint-execution failures are already isolated above.
    private async Task SafeAlertAsync(string title, string body, CancellationToken ct)
    {
        try
        {
            await alerting.SendAlertAsync(title, body, AlertSeverity.Warning, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send alert for {Title}", title);
        }
    }

    public async Task<ReplaySession> ReplayAsync(ReplaySession session, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReplaySessionRepository>();

        session = await replayService.ExecuteReplayAsync(session, ct);
        await repo.AddAsync(session, ct);

        return session;
    }

    public async Task<FuzzerJob> StartFuzzerAsync(FuzzerJob job, CancellationToken ct = default)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFuzzerJobRepository>();
            await repo.AddAsync(job, ct);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningFuzzers[job.Id] = cts;

        _ = Task.Run(() => ExecuteFuzzerAsync(job.Id, cts.Token), cts.Token);

        return job;
    }

    public Task CancelFuzzerAsync(Guid jobId)
    {
        if (_runningFuzzers.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return Task.CompletedTask;
    }

    public bool IsFuzzerRunning(Guid jobId) =>
        _runningFuzzers.ContainsKey(jobId);

    private async Task ExecuteFuzzerAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var fuzzerRepo = scope.ServiceProvider.GetRequiredService<IFuzzerJobRepository>();
            var captureRepo = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();

            var job = await fuzzerRepo.GetByIdAsync(jobId, ct);
            if (job is null) return;

            var baseCapture = await captureRepo.GetByIdAsync(job.BaseCaptureId, ct);
            if (baseCapture is null)
            {
                job.Status = FuzzerJobStatus.Failed;
                job.ErrorMessage = "Base capture not found";
                await fuzzerRepo.UpdateAsync(job, ct);
                return;
            }

            job.Status = FuzzerJobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            await fuzzerRepo.UpdateAsync(job, ct);

            logger.LogInformation("Starting fuzzer {JobId}: {Count} mutations with {Strategy}",
                jobId, job.MutationCount, job.Strategy);
            if (job.BypassTlsValidation)
                logger.LogWarning("Fuzzer {JobId}: TLS validation bypassed (BypassTlsValidation=true)", jobId);

            var semaphore = new SemaphoreSlim(job.ConcurrentRequests);
            var tasks = new List<Task>();
            var anomalyCount = 0;
            var completedCount = 0;

            for (var i = 0; i < job.MutationCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await fuzzerService.ExecuteMutationAsync(baseCapture, index, job.Strategy, job.BypassTlsValidation, ct);
                        result.FuzzerJobId = jobId;
                        await fuzzerRepo.AddResultAsync(result, ct);

                        if (result.IsAnomaly)
                            Interlocked.Increment(ref anomalyCount);

                        Interlocked.Increment(ref completedCount);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);

            // Reload and finalize
            job = await fuzzerRepo.GetByIdAsync(jobId, ct);
            if (job is null) return;

            job.CompletedMutations = completedCount;
            job.Anomalies = anomalyCount;
            job.Status = FuzzerJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await fuzzerRepo.UpdateAsync(job, ct);

            logger.LogInformation("Fuzzer {JobId} completed: {Anomalies}/{Total} anomalies",
                jobId, anomalyCount, job.MutationCount);
        }
        catch (OperationCanceledException)
        {
            await SetFuzzerStatusAsync(jobId, FuzzerJobStatus.Cancelled);
            logger.LogInformation("Fuzzer {JobId} was cancelled", jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fuzzer {JobId} failed", jobId);
            await SetFuzzerStatusAsync(jobId, FuzzerJobStatus.Failed, ex.Message);
        }
        finally
        {
            if (_runningFuzzers.TryRemove(jobId, out var cts))
                cts.Dispose();
        }
    }

    private async Task SetFuzzerStatusAsync(Guid jobId, FuzzerJobStatus status, string? error = null)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFuzzerJobRepository>();
            var job = await repo.GetByIdAsync(jobId);
            if (job is null) return;

            job.Status = status;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ErrorMessage = error;
            await repo.UpdateAsync(job);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update fuzzer job {JobId} status to {Status}", jobId, status);
        }
    }

    private static bool MatchesBreakpoint(Breakpoint bp, HttpMessage message)
    {
        if (bp.HostPattern is not null && !RulesEngine.GetRegex(bp.HostPattern, RegexOptions.IgnoreCase).IsMatch(message.Host))
            return false;
        if (bp.PathPattern is not null && !RulesEngine.GetRegex(bp.PathPattern, RegexOptions.IgnoreCase).IsMatch(message.Path))
            return false;
        return true;
    }
}
