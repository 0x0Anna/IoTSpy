using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Storage;
using IoTSpy.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IoTSpy.Api.Tests.Services;

public class DataRetentionServiceTests
{
    private static (IoTSpyDbContext db, IServiceScopeFactory scopeFactory) CreateDb()
    {
        var services = new ServiceCollection();
        var dbName = $"retention-{Guid.NewGuid():N}";
        services.AddDbContext<IoTSpyDbContext>(opts =>
            opts.UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared"));
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IHostBaselineRepository, HostBaselineRepository>();
        var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<IoTSpyDbContext>();
        db.Database.EnsureCreated();

        return (db, provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static DataRetentionService CreateService(
        IServiceScopeFactory scopeFactory,
        DataRetentionOptions opts)
    {
        var settingsService = new DataRetentionSettingsService(Options.Create(opts));
        return new DataRetentionService(
            scopeFactory,
            settingsService,
            NullLogger<DataRetentionService>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNothing()
    {
        var (db, scopeFactory) = CreateDb();
        var opts = new DataRetentionOptions { Enabled = false };
        var svc = CreateService(scopeFactory, opts);

        // Add a very old capture
        db.Captures.Add(new CapturedRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-365),
            Host = "test.example.com"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Run the service briefly
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await svc.StartAsync(cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await svc.StopAsync(cts.Token);

        // Capture should still exist
        Assert.Equal(1, await db.Captures.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunRetentionPass_DeletesOldCaptures()
    {
        var (db, scopeFactory) = CreateDb();
        var opts = new DataRetentionOptions
        {
            Enabled = true,
            CaptureRetentionDays = 30,
            PacketRetentionDays = 0,
            ScanJobRetentionDays = 0,
            OpenRtbEventRetentionDays = 0,
            RunIntervalHours = 1
        };

        // Old capture (should be deleted)
        db.Captures.Add(new CapturedRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-31),
            Host = "old.example.com"
        });
        // Recent capture (should be kept)
        db.Captures.Add(new CapturedRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
            Host = "new.example.com"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Use InvokeRetentionPassAsync via reflection to test directly
        var svc = CreateService(scopeFactory, opts);
        var method = typeof(DataRetentionService)
            .GetMethod("RunRetentionPassAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [opts, TestContext.Current.CancellationToken])!;

        Assert.Equal(1, await db.Captures.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("new.example.com", (await db.Captures.SingleAsync(TestContext.Current.CancellationToken)).Host);
    }

    [Fact]
    public async Task RunRetentionPass_DeletesOldScanJobs()
    {
        var (db, scopeFactory) = CreateDb();
        var opts = new DataRetentionOptions
        {
            Enabled = true,
            CaptureRetentionDays = 0,
            PacketRetentionDays = 0,
            ScanJobRetentionDays = 7,
            OpenRtbEventRetentionDays = 0,
            RunIntervalHours = 1
        };

        // ScanJob requires a Device FK
        var device = new Core.Models.Device { IpAddress = "10.0.0.1" };
        db.Devices.Add(device);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ScanJobs.Add(new ScanJob
        {
            DeviceId = device.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            Status = Core.Enums.ScanStatus.Completed
        });
        db.ScanJobs.Add(new ScanJob
        {
            DeviceId = device.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            Status = Core.Enums.ScanStatus.Completed
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = CreateService(scopeFactory, opts);
        var method = typeof(DataRetentionService)
            .GetMethod("RunRetentionPassAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [opts, TestContext.Current.CancellationToken])!;

        Assert.Equal(1, await db.ScanJobs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunRetentionPass_DeletesOldProtocolMessages()
    {
        var (db, scopeFactory) = CreateDb();
        var opts = new DataRetentionOptions
        {
            Enabled = true,
            CaptureRetentionDays = 0,
            PacketRetentionDays = 0,
            ScanJobRetentionDays = 0,
            OpenRtbEventRetentionDays = 0,
            ProtocolMessageRetentionDays = 7,
            RunIntervalHours = 1
        };

        db.ProtocolMessages.Add(new PersistedProtocolMessage
        {
            Protocol = Core.Enums.InterceptionProtocol.Mqtt,
            Direction = "client→broker",
            Summary = "old",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10)
        });
        db.ProtocolMessages.Add(new PersistedProtocolMessage
        {
            Protocol = Core.Enums.InterceptionProtocol.Mqtt,
            Direction = "client→broker",
            Summary = "recent",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = CreateService(scopeFactory, opts);
        var method = typeof(DataRetentionService)
            .GetMethod("RunRetentionPassAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [opts, TestContext.Current.CancellationToken])!;

        Assert.Equal(1, await db.ProtocolMessages.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("recent", (await db.ProtocolMessages.SingleAsync(TestContext.Current.CancellationToken)).Summary);
    }

    [Fact]
    public async Task RunRetentionPass_DeletesStaleHostBaselines()
    {
        var (db, scopeFactory) = CreateDb();
        var opts = new DataRetentionOptions
        {
            Enabled = true,
            CaptureRetentionDays = 0,
            PacketRetentionDays = 0,
            ScanJobRetentionDays = 0,
            OpenRtbEventRetentionDays = 0,
            ProtocolMessageRetentionDays = 0,
            HostBaselineRetentionDays = 30,
            RunIntervalHours = 1
        };

        db.HostBaselines.Add(new HostBaselineRecord
        {
            Host = "stale.example.com",
            SampleCount = 10,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-60),
            StatusCodeCountsJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-40),
        });
        db.HostBaselines.Add(new HostBaselineRecord
        {
            Host = "active.example.com",
            SampleCount = 500,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-60),
            StatusCodeCountsJson = "{}",
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1), // still refreshed recently
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = CreateService(scopeFactory, opts);
        var method = typeof(DataRetentionService)
            .GetMethod("RunRetentionPassAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(svc, [opts, TestContext.Current.CancellationToken])!;

        var remaining = await db.HostBaselines.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(remaining);
        Assert.Equal("active.example.com", remaining[0].Host);
    }

    [Fact]
    public void DataRetentionOptions_Defaults_AreReasonable()
    {
        var opts = new DataRetentionOptions();
        Assert.False(opts.Enabled);
        Assert.Equal(30, opts.CaptureRetentionDays);
        Assert.Equal(7, opts.PacketRetentionDays);
        Assert.Equal(90, opts.ScanJobRetentionDays);
        Assert.Equal(14, opts.OpenRtbEventRetentionDays);
        Assert.Equal(30, opts.HostBaselineRetentionDays);
        Assert.Equal(24, opts.RunIntervalHours);
    }
}
