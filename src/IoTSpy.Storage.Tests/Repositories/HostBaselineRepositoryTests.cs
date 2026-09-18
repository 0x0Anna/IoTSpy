using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

public class HostBaselineRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();
    public void Dispose() => _db.Dispose();

    private static HostBaselineRecord MakeRecord(string host = "api.example.com", DateTimeOffset? updatedAt = null) => new()
    {
        Host = host,
        SampleCount = 100,
        FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10),
        DurationMean = 50.0,
        DurationM2 = 25.0,
        SizeMean = 1024.0,
        SizeM2 = 50.0,
        StatusCodeCountsJson = "{\"200\":95,\"500\":5}",
        UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task GetAllAsync_WhenEmpty_ReturnsEmptyList()
    {
        var repo = new HostBaselineRepository(_db);
        var result = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task UpsertAsync_Insert_PersistsRecord()
    {
        var repo = new HostBaselineRepository(_db);
        await repo.UpsertAsync(MakeRecord(), TestContext.Current.CancellationToken);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);
        Assert.Equal("api.example.com", all[0].Host);
        Assert.Equal(100, all[0].SampleCount);
    }

    [Fact]
    public async Task UpsertAsync_ExistingHost_UpdatesInPlace()
    {
        var repo = new HostBaselineRepository(_db);
        await repo.UpsertAsync(MakeRecord(), TestContext.Current.CancellationToken);

        var updated = MakeRecord();
        updated.SampleCount = 250;
        updated.DurationMean = 75.0;
        await repo.UpsertAsync(updated, TestContext.Current.CancellationToken);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all); // still one row, not a duplicate
        Assert.Equal(250, all[0].SampleCount);
        Assert.Equal(75.0, all[0].DurationMean);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        var repo = new HostBaselineRepository(_db);
        await repo.UpsertAsync(MakeRecord(), TestContext.Current.CancellationToken);

        await repo.DeleteAsync("api.example.com", TestContext.Current.CancellationToken);

        var all = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(all);
    }

    [Fact]
    public async Task DeleteAsync_UnknownHost_DoesNotThrow()
    {
        var repo = new HostBaselineRepository(_db);
        var ex = await Xunit.Record.ExceptionAsync(
            () => repo.DeleteAsync("nonexistent.example.com", TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_DeletesOnlyStaleRows()
    {
        var repo = new HostBaselineRepository(_db);
        await repo.UpsertAsync(
            MakeRecord("stale.example.com", DateTimeOffset.UtcNow.AddDays(-40)),
            TestContext.Current.CancellationToken);
        await repo.UpsertAsync(
            MakeRecord("fresh.example.com", DateTimeOffset.UtcNow.AddDays(-1)),
            TestContext.Current.CancellationToken);

        var deleted = await repo.DeleteOlderThanAsync(
            DateTimeOffset.UtcNow.AddDays(-30), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        var remaining = await repo.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(remaining);
        Assert.Equal("fresh.example.com", remaining[0].Host);
    }
}
