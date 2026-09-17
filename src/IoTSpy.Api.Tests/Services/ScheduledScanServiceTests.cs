using System.Reflection;
using IoTSpy.Api.Services;
using IoTSpy.Core.Enums;
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

public class ScheduledScanServiceTests
{
    private static (IoTSpyDbContext db, IServiceScopeFactory scopeFactory) CreateDb(
        IDeviceRepository? deviceRepoOverride = null)
    {
        var services = new ServiceCollection();
        var dbName = $"scheduled-scan-{Guid.NewGuid():N}";
        services.AddDbContext<IoTSpyDbContext>(opts =>
            opts.UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared"));
        services.AddScoped<IScheduledScanRepository, ScheduledScanRepository>();
        services.AddScoped<IScanJobRepository, ScanJobRepository>();
        if (deviceRepoOverride is not null)
            services.AddScoped(_ => deviceRepoOverride);
        else
            services.AddScoped<IDeviceRepository, DeviceRepository>();
        var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<IoTSpyDbContext>();
        db.Database.EnsureCreated();

        return (db, provider.GetRequiredService<IServiceScopeFactory>());
    }

    // The service's background scope reads/writes through a different DbContext
    // instance than the one used to seed/assert in these tests. Without
    // AsNoTracking, a re-query here returns this DbContext's own stale, already-
    // tracked entity from seeding instead of what the other scope actually wrote.
    private static Task<ScheduledScan> GetPersistedScheduleAsync(IoTSpyDbContext db, CancellationToken ct) =>
        db.ScheduledScans.AsNoTracking().SingleAsync(ct);

    private static async Task<ScheduledScan> SeedScheduleAsync(IoTSpyDbContext db)
    {
        var device = new Device { IpAddress = "10.0.0.5" };
        db.Devices.Add(device);

        var schedule = new ScheduledScan
        {
            DeviceId = device.Id,
            CronExpression = "0 * * * *",
            IsEnabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1) // due now
        };
        db.ScheduledScans.Add(schedule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return schedule;
    }

    private static async Task InvokeCheckAndFireScansAsync(
        ScheduledScanService svc, CancellationToken ct)
    {
        var method = typeof(ScheduledScanService)
            .GetMethod("CheckAndFireScansAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [ct])!;
    }

    [Fact]
    public async Task CheckAndFireScansAsync_ScanCompletes_RecordsCompletedStatus()
    {
        var (db, scopeFactory) = CreateDb();
        var schedule = await SeedScheduleAsync(db);

        var scanner = Substitute.For<IScannerService>();
        var startedJob = new ScanJob { DeviceId = schedule.DeviceId, Status = ScanStatus.Pending };
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Persist the job so the polling loop's GetByIdAsync can see it as terminal.
                db.ScanJobs.Add(startedJob);
                db.SaveChanges();
                startedJob.Status = ScanStatus.Completed;
                db.SaveChanges();
                return Task.FromResult(startedJob);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Completed, updated.LastRunStatus);
        Assert.Null(updated.LastRunError);
        Assert.Equal(startedJob.Id, updated.LastScanJobId);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_ScanFails_RecordsFailedStatusAndError()
    {
        var (db, scopeFactory) = CreateDb();
        var schedule = await SeedScheduleAsync(db);

        var scanner = Substitute.For<IScannerService>();
        var startedJob = new ScanJob { DeviceId = schedule.DeviceId, Status = ScanStatus.Pending };
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                db.ScanJobs.Add(startedJob);
                db.SaveChanges();
                startedJob.Status = ScanStatus.Failed;
                startedJob.ErrorMessage = "port scan timed out";
                db.SaveChanges();
                return Task.FromResult(startedJob);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Equal("port scan timed out", updated.LastRunError);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_DeviceMissing_RecordsFailedWithoutStartingScan()
    {
        // A schedule's DeviceId is a real, cascade-deleting FK — an orphaned schedule
        // can't exist in the DB. "Device not found" only arises from a race between
        // reading the enabled-schedules list and looking up the device, so simulate
        // that race by substituting IDeviceRepository to return null despite the
        // device genuinely existing.
        var deviceRepo = Substitute.For<IDeviceRepository>();
        deviceRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Device?)null);
        var (db, scopeFactory) = CreateDb(deviceRepo);
        var schedule = await SeedScheduleAsync(db);

        var scanner = Substitute.For<IScannerService>();
        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Equal("Device not found", updated.LastRunError);
        await scanner.DidNotReceive().StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndFireScansAsync_StartScanThrows_RecordsFailedWithExceptionMessage()
    {
        var (db, scopeFactory) = CreateDb();
        await SeedScheduleAsync(db);

        var scanner = Substitute.For<IScannerService>();
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<ScanJob>>(_ => throw new InvalidOperationException("scanner unavailable"));

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Equal("scanner unavailable", updated.LastRunError);
    }
}
