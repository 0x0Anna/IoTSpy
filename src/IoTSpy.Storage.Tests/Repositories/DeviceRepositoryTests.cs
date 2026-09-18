using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

public class DeviceRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAllAsync_WhenEmpty_ReturnsEmptyList()
    {
        var repo = new DeviceRepository(_db);
        var result = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task UpsertByIpAsync_NewDevice_AddsDevice()
    {
        var repo = new DeviceRepository(_db);
        var device = new Device { IpAddress = "192.168.1.10", Label = "Test" };

        var result = await repo.UpsertByIpAsync(device, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("192.168.1.10", result.IpAddress);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);
    }

    [Fact]
    public async Task UpsertByIpAsync_ExistingIp_UpdatesLastSeen()
    {
        var repo = new DeviceRepository(_db);
        var device = new Device { IpAddress = "192.168.1.20", Label = "First" };
        await repo.UpsertByIpAsync(device, TestContext.Current.CancellationToken);

        var updated = new Device { IpAddress = "192.168.1.20", Label = "Second" };
        await repo.UpsertByIpAsync(updated, TestContext.Current.CancellationToken);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);
    }

    [Fact]
    public async Task UpsertByIpAsync_RecentLastSeenNoMetadataChange_DoesNotRewriteLastSeen()
    {
        // Regression for the SQLite-single-writer proxy-fanout slowdown: a connection that
        // changes nothing (same IP, no new hostname/vendor/MAC, seen moments ago) must not
        // trigger a write at all.
        var repo = new DeviceRepository(_db);
        var inserted = await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.30" }, TestContext.Current.CancellationToken);
        var originalLastSeen = inserted.LastSeen;

        var result = await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.30" }, TestContext.Current.CancellationToken);

        Assert.Equal(originalLastSeen, result.LastSeen);
    }

    [Fact]
    public async Task UpsertByIpAsync_StaleLastSeen_UpdatesLastSeen()
    {
        var repo = new DeviceRepository(_db);
        var inserted = await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.31" }, TestContext.Current.CancellationToken);

        // Simulate the device having gone stale (older than the 30s write threshold).
        inserted.LastSeen = DateTimeOffset.UtcNow.AddSeconds(-60);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.31" }, TestContext.Current.CancellationToken);

        Assert.True(result.LastSeen > DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task UpsertByIpAsync_MetadataChanged_UpdatesEvenWithFreshLastSeen()
    {
        // A hostname/vendor/MAC change is worth persisting immediately regardless of how
        // recently LastSeen was written — only a true no-op connection should be skipped.
        var repo = new DeviceRepository(_db);
        await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.32" }, TestContext.Current.CancellationToken);

        var result = await repo.UpsertByIpAsync(
            new Device { IpAddress = "192.168.1.32", Hostname = "new-hostname.local" },
            TestContext.Current.CancellationToken);

        Assert.Equal("new-hostname.local", result.Hostname);
    }

    [Fact]
    public async Task GetByIdAsync_WhenFound_ReturnsDevice()
    {
        var repo = new DeviceRepository(_db);
        var inserted = await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.0.1" }, TestContext.Current.CancellationToken);

        var result = await repo.GetByIdAsync(inserted.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("10.0.0.1", result.IpAddress);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var repo = new DeviceRepository(_db);
        var result = await repo.GetByIdAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIpAsync_WhenFound_ReturnsDevice()
    {
        var repo = new DeviceRepository(_db);
        await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.0.2" }, TestContext.Current.CancellationToken);

        var result = await repo.GetByIpAsync("10.0.0.2", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("10.0.0.2", result.IpAddress);
    }

    [Fact]
    public async Task GetByIpAsync_WhenNotFound_ReturnsNull()
    {
        var repo = new DeviceRepository(_db);
        var result = await repo.GetByIpAsync("99.99.99.99", TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var repo = new DeviceRepository(_db);
        var device = await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.0.3", Label = "Old" }, TestContext.Current.CancellationToken);

        device.Label = "Updated";
        await repo.UpdateAsync(device, TestContext.Current.CancellationToken);

        var refetched = await repo.GetByIdAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Updated", refetched!.Label);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDevice()
    {
        var repo = new DeviceRepository(_db);
        var device = await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.0.4" }, TestContext.Current.CancellationToken);

        await repo.DeleteAsync(device.Id, TestContext.Current.CancellationToken);

        var result = await repo.GetByIdAsync(device.Id, TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsMultipleDevices()
    {
        var repo = new DeviceRepository(_db);
        await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.1.1" }, TestContext.Current.CancellationToken);
        await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.1.2" }, TestContext.Current.CancellationToken);
        await repo.UpsertByIpAsync(new Device { IpAddress = "10.0.1.3" }, TestContext.Current.CancellationToken);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
    }
}
