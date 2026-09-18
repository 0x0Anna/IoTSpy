using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Storage;
using IoTSpy.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Services;

public class HostBaselineCheckpointServiceTests
{
    private static (IoTSpyDbContext db, IServiceScopeFactory scopeFactory) CreateDb()
    {
        var services = new ServiceCollection();
        var dbName = $"host-baseline-{Guid.NewGuid():N}";
        services.AddDbContext<IoTSpyDbContext>(opts =>
            opts.UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared"));
        services.AddScoped<IHostBaselineRepository, HostBaselineRepository>();
        var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<IoTSpyDbContext>();
        db.Database.EnsureCreated();

        return (db, provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static HostBaselineSnapshot MakeSnapshot(string host, long sampleCount = 42) => new()
    {
        Host = host,
        SampleCount = sampleCount,
        FirstSeenAt = DateTimeOffset.UtcNow.AddHours(-1),
        DurationMean = 100.0,
        DurationM2 = 10.0,
        SizeMean = 1024.0,
        SizeM2 = 10.0,
        StatusCodeCounts = new Dictionary<int, long> { [200] = sampleCount },
    };

    [Fact]
    public async Task StartAsync_SeedsAnomalyDetectorFromPersistedRows()
    {
        var (db, scopeFactory) = CreateDb();
        db.HostBaselines.Add(new HostBaselineRecord
        {
            Host = "restored.example.com",
            SampleCount = 500,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-5),
            StatusCodeCountsJson = "{\"200\":500}",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detector = Substitute.For<IAnomalyDetector>();
        detector.SnapshotBaselines().Returns([]);

        var svc = new HostBaselineCheckpointService(
            scopeFactory, detector, NullLogger<HostBaselineCheckpointService>.Instance);

        await svc.StartAsync(TestContext.Current.CancellationToken);
        await svc.StopAsync(TestContext.Current.CancellationToken);

        detector.Received(1).Seed(Arg.Is<IEnumerable<HostBaselineRecord>>(
            recs => recs.Count() == 1 && recs.Single().Host == "restored.example.com"));
    }

    [Fact]
    public async Task StopAsync_FlushesSnapshotsToRepository()
    {
        var (db, scopeFactory) = CreateDb();

        var detector = Substitute.For<IAnomalyDetector>();
        detector.SnapshotBaselines().Returns([MakeSnapshot("live.example.com")]);

        var svc = new HostBaselineCheckpointService(
            scopeFactory, detector, NullLogger<HostBaselineCheckpointService>.Instance);

        await svc.StartAsync(TestContext.Current.CancellationToken);
        // Stop immediately — verifies the graceful-shutdown final flush, not the periodic timer.
        await svc.StopAsync(TestContext.Current.CancellationToken);

        var persisted = await db.HostBaselines.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(persisted);
        Assert.Equal("live.example.com", persisted[0].Host);
        Assert.Equal(42, persisted[0].SampleCount);
    }

    [Fact]
    public async Task StopAsync_DeletesPersistedRowsNoLongerInLiveSnapshot()
    {
        var (db, scopeFactory) = CreateDb();
        // Pre-existing persisted row for a host that Reset() has since removed from memory.
        db.HostBaselines.Add(new HostBaselineRecord
        {
            Host = "reset.example.com",
            SampleCount = 10,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
            StatusCodeCountsJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var detector = Substitute.For<IAnomalyDetector>();
        // Live snapshot no longer contains "reset.example.com" — only a different host.
        detector.SnapshotBaselines().Returns([MakeSnapshot("still-live.example.com")]);

        var svc = new HostBaselineCheckpointService(
            scopeFactory, detector, NullLogger<HostBaselineCheckpointService>.Instance);

        await svc.StartAsync(TestContext.Current.CancellationToken);
        await svc.StopAsync(TestContext.Current.CancellationToken);

        var persisted = await db.HostBaselines.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(persisted);
        Assert.Equal("still-live.example.com", persisted[0].Host);
    }

    [Fact]
    public async Task RoundTrip_FlushThenRecover_RestoresSameState()
    {
        var (db, scopeFactory) = CreateDb();

        var detector1 = Substitute.For<IAnomalyDetector>();
        detector1.SnapshotBaselines().Returns([MakeSnapshot("roundtrip.example.com", 777)]);

        var svc1 = new HostBaselineCheckpointService(
            scopeFactory, detector1, NullLogger<HostBaselineCheckpointService>.Instance);
        await svc1.StartAsync(TestContext.Current.CancellationToken);
        await svc1.StopAsync(TestContext.Current.CancellationToken);

        // A fresh detector instance (simulating a restart) should be seeded with the
        // exact same host/sample count that was flushed above.
        var detector2 = Substitute.For<IAnomalyDetector>();
        detector2.SnapshotBaselines().Returns([]);

        var svc2 = new HostBaselineCheckpointService(
            scopeFactory, detector2, NullLogger<HostBaselineCheckpointService>.Instance);
        await svc2.StartAsync(TestContext.Current.CancellationToken);
        await svc2.StopAsync(TestContext.Current.CancellationToken);

        detector2.Received(1).Seed(Arg.Is<IEnumerable<HostBaselineRecord>>(
            recs => recs.Count() == 1 &&
                    recs.Single().Host == "roundtrip.example.com" &&
                    recs.Single().SampleCount == 777));
    }
}
