using System.Text.RegularExpressions;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Xunit;

namespace IoTSpy.Storage.Tests;

/// <summary>
/// Verifies <see cref="DbQueryMetricsInterceptor"/> records EF Core command duration
/// into <see cref="StorageMetrics"/>'s iotspy_db_query_duration_seconds histogram.
/// Mirrors TestDbContextFactory's in-memory-SQLite convention, but builds its own
/// context so the interceptor can be registered.
/// </summary>
public class DbQueryMetricsInterceptorTests : IDisposable
{
    private readonly IoTSpyDbContext _db;

    public DbQueryMetricsInterceptorTests()
    {
        var dbName = $"metrics-test-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<IoTSpyDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .AddInterceptors(new DbQueryMetricsInterceptor())
            .Options;

        _db = new IoTSpyDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private static async Task<string> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // The histogram has no labels, so exactly one "_count <value>" line exists;
    // parse it to prove an execution genuinely added an observation (rather than
    // just asserting the metric name is present, which would pass even if this
    // test's own command was never timed).
    private static double ExtractCount(string scrapedText)
    {
        var match = Regex.Match(scrapedText, @"iotspy_db_query_duration_seconds_count (\d+(\.\d+)?)");
        return match.Success ? double.Parse(match.Groups[1].Value) : 0;
    }

    [Fact]
    public async Task Query_RecordsDbQueryDurationHistogram()
    {
        var before = ExtractCount(await ScrapeAsync());

        // A reader command (SELECT via LINQ) exercises ReaderExecuting(Async)/ReaderExecuted(Async).
        _ = await _db.Devices.ToListAsync(TestContext.Current.CancellationToken);

        var after = ExtractCount(await ScrapeAsync());
        Assert.True(after > before, $"Expected histogram count to increase (before={before}, after={after})");
    }

    [Fact]
    public async Task NonQuery_RecordsDbQueryDurationHistogram()
    {
        var before = ExtractCount(await ScrapeAsync());

        // AddAsync + SaveChangesAsync exercises the NonQueryExecuting(Async)/NonQueryExecuted(Async) path.
        await _db.Devices.AddAsync(new Device { IpAddress = "10.0.0.5", Label = "metrics-test" }, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var after = ExtractCount(await ScrapeAsync());
        Assert.True(after > before, $"Expected histogram count to increase (before={before}, after={after})");
    }
}
