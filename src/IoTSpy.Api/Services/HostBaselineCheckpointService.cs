using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IoTSpy.Api.Services;

/// <summary>
/// Periodically checkpoints <see cref="IAnomalyDetector"/>'s in-memory host baselines to
/// storage, and restores them at startup, so the anomaly detector doesn't lose its learned
/// baseline (and re-enter warm-up) on every API restart. Modeled on
/// <c>IoTSpy.Scanner.PacketCaptureCheckpointService</c>.
///
/// Baseline drift is slow, so this runs on a much longer cadence (30s) than the packet
/// checkpoint service (1s). A final flush on graceful shutdown limits data loss on a clean
/// restart to effectively zero, rather than up to one full interval.
/// </summary>
public sealed class HostBaselineCheckpointService(
    IServiceScopeFactory scopeFactory,
    IAnomalyDetector anomalyDetector,
    ILogger<HostBaselineCheckpointService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverFromDatabaseAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Best-effort final flush so a graceful shutdown doesn't lose up to 30s of
        // learned baseline. Swallow failures — shutdown must proceed regardless.
        try
        {
            await FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HostBaselineCheckpoint: final flush on shutdown failed");
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await FlushAsync(stoppingToken);
        }
    }

    private async Task RecoverFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IHostBaselineRepository>();

            var records = await repo.GetAllAsync(ct);
            if (records.Count > 0)
            {
                anomalyDetector.Seed(records);
                logger.LogInformation(
                    "HostBaselineCheckpoint: restored {Count} host baselines from DB", records.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HostBaselineCheckpoint: startup recovery failed");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var snapshots = anomalyDetector.SnapshotBaselines();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IHostBaselineRepository>();

            var now = DateTimeOffset.UtcNow;
            var liveHosts = new HashSet<string>(snapshots.Count);

            foreach (var snapshot in snapshots)
            {
                liveHosts.Add(snapshot.Host);
                await repo.UpsertAsync(new HostBaselineRecord
                {
                    Host = snapshot.Host,
                    SampleCount = snapshot.SampleCount,
                    FirstSeenAt = snapshot.FirstSeenAt,
                    DurationMean = snapshot.DurationMean,
                    DurationM2 = snapshot.DurationM2,
                    SizeMean = snapshot.SizeMean,
                    SizeM2 = snapshot.SizeM2,
                    StatusCodeCountsJson = System.Text.Json.JsonSerializer.Serialize(snapshot.StatusCodeCounts),
                    UpdatedAt = now,
                }, ct);
            }

            // Clean up persisted rows for hosts that are no longer in the live in-memory
            // dictionary — this is how AnomalyDetector.Reset(host) (which only removes the
            // host from memory and has no way to reach into storage from IoTSpy.Protocols)
            // gets its persisted row cleaned up, without AnomalyDetector ever needing a
            // Storage dependency.
            var persisted = await repo.GetAllAsync(ct);
            foreach (var row in persisted)
            {
                if (!liveHosts.Contains(row.Host))
                    await repo.DeleteAsync(row.Host, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "HostBaselineCheckpoint: flush failed ({Count} hosts)", snapshots.Count);
        }
    }
}
