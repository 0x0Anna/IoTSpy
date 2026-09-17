using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

public class CaptureRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    private static CapturedRequest MakeCapture(string host = "example.com", string method = "GET", int status = 200) =>
        new()
        {
            Host = host,
            Method = method,
            Path = "/test",
            StatusCode = status,
            Timestamp = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task AddAsync_AddsCapture()
    {
        var repo = new CaptureRepository(_db);
        var capture = MakeCapture();

        var result = await repo.AddAsync(capture, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(1, await repo.CountAsync(new CaptureFilter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetByIdAsync_WhenFound_ReturnsCapture()
    {
        var repo = new CaptureRepository(_db);
        var capture = await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);

        var result = await repo.GetByIdAsync(capture.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(capture.Id, result.Id);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var repo = new CaptureRepository(_db);
        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPagedAsync_ReturnsCorrectPage()
    {
        var repo = new CaptureRepository(_db);
        for (int i = 0; i < 5; i++)
            await repo.AddAsync(MakeCapture($"host{i}.com"), TestContext.Current.CancellationToken);

        var page1 = await repo.GetPagedAsync(new CaptureFilter(), 1, 3, TestContext.Current.CancellationToken);
        var page2 = await repo.GetPagedAsync(new CaptureFilter(), 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, page1.Count);
        Assert.Equal(2, page2.Count);
    }

    [Fact]
    public async Task CountAsync_ReturnsCorrectTotal()
    {
        var repo = new CaptureRepository(_db);
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);

        Assert.Equal(2, await repo.CountAsync(new CaptureFilter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPagedAsync_FilterByHost_ReturnsFiltered()
    {
        var repo = new CaptureRepository(_db);
        await repo.AddAsync(MakeCapture("api.example.com"), TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture("other.com"), TestContext.Current.CancellationToken);

        var filter = new CaptureFilter(HostContains: "example");
        var results = await repo.GetPagedAsync(filter, 1, 50, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("api.example.com", results[0].Host);
    }

    [Fact]
    public async Task GetPagedAsync_FilterByStatusCode_ReturnsFiltered()
    {
        var repo = new CaptureRepository(_db);
        await repo.AddAsync(MakeCapture(status: 200), TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture(status: 404), TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture(status: 200), TestContext.Current.CancellationToken);

        var filter = new CaptureFilter(StatusCode: 404);
        var results = await repo.GetPagedAsync(filter, 1, 50, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(404, results[0].StatusCode);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCapture()
    {
        var repo = new CaptureRepository(_db);
        var capture = await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);

        await repo.DeleteAsync(capture.Id, TestContext.Current.CancellationToken);

        Assert.Null(await repo.GetByIdAsync(capture.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearAsync_WithNoFilter_RemovesAll()
    {
        var repo = new CaptureRepository(_db);
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);

        await repo.ClearAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, await repo.CountAsync(new CaptureFilter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearAsync_WithDeviceId_OnlyClearsMatchingDevice()
    {
        var repo = new CaptureRepository(_db);

        // Insert a real device so foreign key constraint is satisfied
        var device = new IoTSpy.Core.Models.Device { IpAddress = "10.0.0.1" };
        _db.Devices.Add(device);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var c1 = MakeCapture(); c1.DeviceId = device.Id;
        var c2 = MakeCapture(); c2.DeviceId = device.Id;
        await repo.AddAsync(c1, TestContext.Current.CancellationToken);
        await repo.AddAsync(c2, TestContext.Current.CancellationToken);
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);  // No device

        await repo.ClearAsync(device.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, await repo.CountAsync(new CaptureFilter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPagedAsync_FilterByHeaderSearch_MatchesRequestHeaders()
    {
        var repo = new CaptureRepository(_db);
        var c1 = MakeCapture();
        c1.RequestHeaders = "{\"Authorization\":\"Bearer token123\"}";
        var c2 = MakeCapture();
        c2.RequestHeaders = "{\"Content-Type\":\"application/json\"}";
        await repo.AddAsync(c1, TestContext.Current.CancellationToken);
        await repo.AddAsync(c2, TestContext.Current.CancellationToken);

        var results = await repo.GetPagedAsync(new CaptureFilter(HeaderSearch: "Bearer"), 1, 50, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Contains("Bearer", results[0].RequestHeaders);
    }

    [Fact]
    public async Task GetPagedAsync_FilterByHeaderSearch_MatchesResponseHeaders()
    {
        var repo = new CaptureRepository(_db);
        var c1 = MakeCapture();
        c1.ResponseHeaders = "{\"X-Custom-Header\":\"secret-value\"}";
        var c2 = MakeCapture();
        c2.ResponseHeaders = "{\"Content-Type\":\"text/html\"}";
        await repo.AddAsync(c1, TestContext.Current.CancellationToken);
        await repo.AddAsync(c2, TestContext.Current.CancellationToken);

        var results = await repo.GetPagedAsync(new CaptureFilter(HeaderSearch: "secret-value"), 1, 50, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Contains("secret-value", results[0].ResponseHeaders);
    }

    [Fact]
    public async Task CountAsync_FilterByHeaderSearch_CountsCorrectly()
    {
        var repo = new CaptureRepository(_db);
        for (var i = 0; i < 3; i++)
        {
            var c = MakeCapture();
            c.RequestHeaders = "{\"Authorization\":\"Bearer abc\"}";
            await repo.AddAsync(c, TestContext.Current.CancellationToken);
        }
        await repo.AddAsync(MakeCapture(), TestContext.Current.CancellationToken);

        Assert.Equal(3, await repo.CountAsync(new CaptureFilter(HeaderSearch: "Bearer"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPagedAsync_FilterByBodySearch_MatchesRequestOrResponseBody()
    {
        var repo = new CaptureRepository(_db);
        var c1 = MakeCapture();
        c1.RequestBody = "{\"apiKey\":\"sk-secret-token\"}";
        var c2 = MakeCapture();
        c2.ResponseBody = "{\"apiKey\":\"sk-secret-token\"}";
        var c3 = MakeCapture();
        c3.RequestBody = "{\"unrelated\":\"value\"}";
        await repo.AddAsync(c1, TestContext.Current.CancellationToken);
        await repo.AddAsync(c2, TestContext.Current.CancellationToken);
        await repo.AddAsync(c3, TestContext.Current.CancellationToken);

        var results = await repo.GetPagedAsync(new CaptureFilter(BodySearch: "sk-secret-token"), 1, 50, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, await repo.CountAsync(new CaptureFilter(BodySearch: "sk-secret-token"), TestContext.Current.CancellationToken));
    }
}
