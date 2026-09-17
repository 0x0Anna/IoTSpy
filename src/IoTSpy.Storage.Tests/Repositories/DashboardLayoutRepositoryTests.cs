using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

public class DashboardLayoutRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private static DashboardLayout MakeLayout(Guid userId, string name = "Layout") => new()
    {
        UserId = userId,
        Name = name,
        LayoutJson = "{}",
        FiltersJson = "{}"
    };

    [Fact]
    public async Task GetByUserAsync_OnlyReturnsLayoutsForGivenUser()
    {
        var repo = new DashboardLayoutRepository(_db);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await repo.CreateAsync(MakeLayout(userA, "A1"), TestContext.Current.CancellationToken);
        await repo.CreateAsync(MakeLayout(userA, "A2"), TestContext.Current.CancellationToken);
        await repo.CreateAsync(MakeLayout(userB, "B1"), TestContext.Current.CancellationToken);

        var results = await repo.GetByUserAsync(userA, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, l => Assert.Equal(userA, l.UserId));
    }

    [Fact]
    public async Task GetByUserAsync_ForUserWithNoLayouts_ReturnsEmpty()
    {
        var repo = new DashboardLayoutRepository(_db);
        await repo.CreateAsync(MakeLayout(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var results = await repo.GetByUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CreateAsync_ThenGetByIdAsync_RoundTrips()
    {
        var repo = new DashboardLayoutRepository(_db);
        var layout = MakeLayout(Guid.NewGuid(), "Round Trip");

        await repo.CreateAsync(layout, TestContext.Current.CancellationToken);
        var fetched = await repo.GetByIdAsync(layout.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(fetched);
        Assert.Equal(layout.Id, fetched!.Id);
        Assert.Equal("Round Trip", fetched.Name);
    }

    [Fact]
    public async Task GetByIdAsync_WhenMissing_ReturnsNull()
    {
        var repo = new DashboardLayoutRepository(_db);

        var fetched = await repo.GetByIdAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task UpdateAsync_BumpsUpdatedAt()
    {
        var repo = new DashboardLayoutRepository(_db);
        var layout = MakeLayout(Guid.NewGuid());
        await repo.CreateAsync(layout, TestContext.Current.CancellationToken);
        var originalUpdatedAt = layout.UpdatedAt;

        await Task.Delay(10, TestContext.Current.CancellationToken);
        layout.Name = "Updated Name";
        var updated = await repo.UpdateAsync(layout, TestContext.Current.CancellationToken);

        Assert.True(updated.UpdatedAt > originalUpdatedAt);
        var fetched = await repo.GetByIdAsync(layout.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Updated Name", fetched!.Name);
    }

    [Fact]
    public async Task DeleteAsync_RemovesLayout()
    {
        var repo = new DashboardLayoutRepository(_db);
        var layout = MakeLayout(Guid.NewGuid());
        await repo.CreateAsync(layout, TestContext.Current.CancellationToken);

        await repo.DeleteAsync(layout.Id, TestContext.Current.CancellationToken);
        var fetched = await repo.GetByIdAsync(layout.Id, TestContext.Current.CancellationToken);

        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_WhenMissing_DoesNotThrow()
    {
        var repo = new DashboardLayoutRepository(_db);

        var exception = await Record.ExceptionAsync(() =>
            repo.DeleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }
}
