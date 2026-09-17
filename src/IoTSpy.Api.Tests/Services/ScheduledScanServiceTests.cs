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
        services.AddScoped<IScanScopeRepository, ScanScopeRepository>();
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
        var startedJob = new ScanJob { DeviceId = schedule.DeviceId!.Value, Status = ScanStatus.Pending };
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
        var startedJob = new ScanJob { DeviceId = schedule.DeviceId!.Value, Status = ScanStatus.Pending };
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

    [Fact]
    public async Task CheckAndFireScansAsync_DeviceOutsideActiveScope_SkipsScanAndRecordsFailed()
    {
        // Regression test: a scheduled scan must honour the same scan-scope consent
        // gate ScannerController.StartScan enforces for direct, interactive scans —
        // otherwise scheduling is a way to bypass it entirely.
        var (db, scopeFactory) = CreateDb();
        var schedule = await SeedScheduleAsync(db); // device IP is 10.0.0.5

        db.ScanScopes.Add(new ScanScope { Cidr = "192.168.0.0/24", IsActive = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scanner = Substitute.For<IScannerService>();
        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Contains("scan scope", updated.LastRunError, StringComparison.OrdinalIgnoreCase);
        await scanner.DidNotReceive().StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndFireScansAsync_DeviceInsideActiveScope_StillFires()
    {
        var (db, scopeFactory) = CreateDb();
        var schedule = await SeedScheduleAsync(db); // device IP is 10.0.0.5

        db.ScanScopes.Add(new ScanScope { Cidr = "10.0.0.0/24", IsActive = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scanner = Substitute.For<IScannerService>();
        var startedJob = new ScanJob { DeviceId = schedule.DeviceId!.Value, Status = ScanStatus.Completed };
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                db.ScanJobs.Add(startedJob);
                db.SaveChanges();
                return Task.FromResult(startedJob);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Completed, updated.LastRunStatus);
        await scanner.Received(1).StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>());
    }

    private static async Task<ScheduledScan> SeedMultiDeviceScheduleAsync(
        IoTSpyDbContext db, string? targetCidr = null, string? targetTag = null, params Device[] devices)
    {
        db.Devices.AddRange(devices);

        var schedule = new ScheduledScan
        {
            DeviceId = null,
            TargetCidr = targetCidr,
            TargetTag = targetTag,
            CronExpression = "0 * * * *",
            IsEnabled = true,
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1) // due now
        };
        db.ScheduledScans.Add(schedule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return schedule;
    }

    [Fact]
    public async Task CheckAndFireScansAsync_CidrTarget_StartsOneScanPerMatchingDevice()
    {
        var (db, scopeFactory) = CreateDb();
        var inRange1 = new Device { IpAddress = "10.0.0.5" };
        var inRange2 = new Device { IpAddress = "10.0.0.6" };
        var outOfRange = new Device { IpAddress = "192.168.1.1" };
        await SeedMultiDeviceScheduleAsync(db, targetCidr: "10.0.0.0/24", devices: [inRange1, inRange2, outOfRange]);

        var scanner = Substitute.For<IScannerService>();
        var startedJobs = new List<ScanJob>();
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.Arg<ScanJob>();
                var job = new ScanJob { DeviceId = requested.DeviceId, Status = ScanStatus.Completed };
                startedJobs.Add(job);
                db.ScanJobs.Add(job);
                db.SaveChanges();
                return Task.FromResult(job);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        // Only the two in-range devices should have been scanned.
        Assert.Equal(2, startedJobs.Count);
        Assert.Contains(startedJobs, j => j.DeviceId == inRange1.Id);
        Assert.Contains(startedJobs, j => j.DeviceId == inRange2.Id);
        Assert.DoesNotContain(startedJobs, j => j.DeviceId == outOfRange.Id);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Completed, updated.LastRunStatus);
        Assert.Null(updated.LastRunError);
        Assert.NotNull(updated.LastScanJobId);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_TagTarget_StartsOneScanPerMatchingDevice()
    {
        var (db, scopeFactory) = CreateDb();
        var camera1 = new Device { IpAddress = "10.0.0.5", Tags = "camera,upstairs" };
        var camera2 = new Device { IpAddress = "10.0.0.6", Tags = "camera" };
        var thermostat = new Device { IpAddress = "10.0.0.7", Tags = "thermostat" };
        await SeedMultiDeviceScheduleAsync(db, targetTag: "camera", devices: [camera1, camera2, thermostat]);

        var scanner = Substitute.For<IScannerService>();
        var startedJobs = new List<ScanJob>();
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.Arg<ScanJob>();
                var job = new ScanJob { DeviceId = requested.DeviceId, Status = ScanStatus.Completed };
                startedJobs.Add(job);
                db.ScanJobs.Add(job);
                db.SaveChanges();
                return Task.FromResult(job);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        Assert.Equal(2, startedJobs.Count);
        Assert.Contains(startedJobs, j => j.DeviceId == camera1.Id);
        Assert.Contains(startedJobs, j => j.DeviceId == camera2.Id);
        Assert.DoesNotContain(startedJobs, j => j.DeviceId == thermostat.Id);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_CidrTarget_SkipsDevicesOutsideActiveScopeButScansTheRest()
    {
        // Regression test: multi-device (CIDR/tag) schedules must apply the same
        // per-device scan-scope gate as single-device schedules and direct scans —
        // a device outside every active scope is skipped, not fatal to the batch.
        var (db, scopeFactory) = CreateDb();
        var inScope = new Device { IpAddress = "10.0.0.5" };
        var outOfScope = new Device { IpAddress = "10.0.0.99" };
        await SeedMultiDeviceScheduleAsync(db, targetCidr: "10.0.0.0/24", devices: [inScope, outOfScope]);

        db.ScanScopes.Add(new ScanScope { Cidr = "10.0.0.0/28", IsActive = true }); // covers .5, not .99
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scanner = Substitute.For<IScannerService>();
        var startedJobs = new List<ScanJob>();
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.Arg<ScanJob>();
                var job = new ScanJob { DeviceId = requested.DeviceId, Status = ScanStatus.Completed };
                startedJobs.Add(job);
                db.ScanJobs.Add(job);
                db.SaveChanges();
                return Task.FromResult(job);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        Assert.Single(startedJobs);
        Assert.Equal(inScope.Id, startedJobs[0].DeviceId);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Completed, updated.LastRunStatus);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_CidrTarget_AllDevicesOutsideScope_RecordsFailedWithoutScanning()
    {
        var (db, scopeFactory) = CreateDb();
        var outOfScope = new Device { IpAddress = "10.0.0.99" };
        await SeedMultiDeviceScheduleAsync(db, targetCidr: "10.0.0.0/24", devices: [outOfScope]);

        db.ScanScopes.Add(new ScanScope { Cidr = "192.168.0.0/24", IsActive = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scanner = Substitute.For<IScannerService>();
        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        await scanner.DidNotReceive().StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndFireScansAsync_CidrTarget_NoMatchingDevices_RecordsFailed()
    {
        var (db, scopeFactory) = CreateDb();
        var nonMatching = new Device { IpAddress = "192.168.1.1" };
        await SeedMultiDeviceScheduleAsync(db, targetCidr: "10.0.0.0/24", devices: [nonMatching]);

        var scanner = Substitute.For<IScannerService>();
        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Equal("No matching devices found", updated.LastRunError);
    }

    [Fact]
    public async Task CheckAndFireScansAsync_CidrTarget_OneDeviceFails_RecordsOverallFailedWithSummary()
    {
        var (db, scopeFactory) = CreateDb();
        var good = new Device { IpAddress = "10.0.0.5" };
        var bad = new Device { IpAddress = "10.0.0.6" };
        await SeedMultiDeviceScheduleAsync(db, targetCidr: "10.0.0.0/24", devices: [good, bad]);

        var scanner = Substitute.For<IScannerService>();
        scanner.StartScanAsync(Arg.Any<ScanJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var requested = ci.Arg<ScanJob>();
                var status = requested.DeviceId == bad.Id ? ScanStatus.Failed : ScanStatus.Completed;
                var job = new ScanJob
                {
                    DeviceId = requested.DeviceId,
                    Status = status,
                    ErrorMessage = status == ScanStatus.Failed ? "port scan timed out" : null
                };
                db.ScanJobs.Add(job);
                db.SaveChanges();
                return Task.FromResult(job);
            });

        var alerting = Substitute.For<IAlertingService>();
        var svc = new ScheduledScanService(scopeFactory, scanner, alerting, NullLogger<ScheduledScanService>.Instance);

        await InvokeCheckAndFireScansAsync(svc, TestContext.Current.CancellationToken);

        var updated = await GetPersistedScheduleAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal(ScanStatus.Failed, updated.LastRunStatus);
        Assert.Contains("10.0.0.6", updated.LastRunError);
        Assert.Contains("port scan timed out", updated.LastRunError);
    }
}
