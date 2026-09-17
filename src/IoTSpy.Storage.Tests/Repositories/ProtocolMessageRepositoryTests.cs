using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

public class ProtocolMessageRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private static PersistedProtocolMessage MakeMessage(
        Guid? deviceId = null, InterceptionProtocol protocol = InterceptionProtocol.Mqtt, DateTimeOffset? timestamp = null) =>
        new()
        {
            DeviceId = deviceId,
            Protocol = protocol,
            Direction = "client→broker",
            Subject = "sensors/temp",
            Summary = "PUBLISH qos=1 retain=false payloadLen=12",
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task AddBatchAsync_PersistsAllMessages()
    {
        var repo = new ProtocolMessageRepository(_db);
        var deviceId = Guid.NewGuid();

        await repo.AddBatchAsync([MakeMessage(deviceId), MakeMessage(deviceId)], TestContext.Current.CancellationToken);

        var results = await repo.GetByDeviceIdAsync(deviceId, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetByDeviceIdAsync_OnlyReturnsMatchingDevice()
    {
        var repo = new ProtocolMessageRepository(_db);
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        await repo.AddBatchAsync([MakeMessage(deviceA), MakeMessage(deviceB)], TestContext.Current.CancellationToken);

        var results = await repo.GetByDeviceIdAsync(deviceA, ct: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(deviceA, results[0].DeviceId);
    }

    [Fact]
    public async Task GetByDeviceIdAsync_FiltersByDateRange()
    {
        var repo = new ProtocolMessageRepository(_db);
        var deviceId = Guid.NewGuid();
        var old = MakeMessage(deviceId, timestamp: DateTimeOffset.UtcNow.AddDays(-10));
        var recent = MakeMessage(deviceId, timestamp: DateTimeOffset.UtcNow);
        await repo.AddBatchAsync([old, recent], TestContext.Current.CancellationToken);

        var results = await repo.GetByDeviceIdAsync(
            deviceId, from: DateTimeOffset.UtcNow.AddDays(-1), ct: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(recent.Id, results[0].Id);
    }

    [Fact]
    public async Task GetByDeviceIdsAsync_AggregatesAcrossMultipleDevices()
    {
        var repo = new ProtocolMessageRepository(_db);
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var deviceC = Guid.NewGuid();
        await repo.AddBatchAsync(
            [MakeMessage(deviceA), MakeMessage(deviceB), MakeMessage(deviceC)],
            TestContext.Current.CancellationToken);

        var results = await repo.GetByDeviceIdsAsync([deviceA, deviceB], ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, m => m.DeviceId == deviceC);
    }

    [Fact]
    public async Task GetByDeviceIdAsync_MessageWithNullDeviceId_IsNotReturned()
    {
        var repo = new ProtocolMessageRepository(_db);
        var deviceId = Guid.NewGuid();
        await repo.AddBatchAsync([MakeMessage(deviceId: null)], TestContext.Current.CancellationToken);

        var results = await repo.GetByDeviceIdAsync(deviceId, ct: TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }
}
