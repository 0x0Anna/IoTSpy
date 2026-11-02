using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IoTSpy.Storage.Tests;

/// <summary>
/// The interceptor is provider-agnostic, so it is exercised on in-memory SQLite (which, unlike
/// Postgres, would happily store a NUL, making the stored value easy to inspect).
/// </summary>
public class PostgresTextSanitizerInterceptorTests : IDisposable
{
    private readonly string _dbName = $"sanitizer-test-{Guid.NewGuid():N}";
    private readonly IoTSpyDbContext _db;

    public PostgresTextSanitizerInterceptorTests()
    {
        _db = CreateContext();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private IoTSpyDbContext CreateContext() => new(new DbContextOptionsBuilder<IoTSpyDbContext>()
        .UseSqlite($"Data Source=file:{_dbName}?mode=memory&cache=shared")
        .AddInterceptors(new PostgresTextSanitizerInterceptor())
        .Options);

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    [InlineData("a\0b", "a�b")]
    [InlineData("\0\0\0", "���")]
    [InlineData("GIF89a\0\0\x01\0", "GIF89a��\x01�")]
    public void Sanitize_ReplacesNulCharacters(string input, string expected)
        => Assert.Equal(expected, PostgresTextSanitizerInterceptor.Sanitize(input));

    [Fact]
    public void Sanitize_WithoutNul_ReturnsTheSameInstance()
    {
        var text = new string('x', 100);
        Assert.Same(text, PostgresTextSanitizerInterceptor.Sanitize(text));
    }

    [Fact]
    public async Task AddedEntity_HasNulReplacedInEveryStringProperty()
    {
        var id = Guid.NewGuid();
        _db.Captures.Add(new CapturedRequest
        {
            Id = id,
            Method = "GET", Host = "example.test", Path = "/img.gif",
            RequestHeaders = "Accept: \0*/*",
            ResponseBody = "GIF89a\0\0data\0",
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = CreateContext();
        var saved = await read.Captures.AsNoTracking().SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);

        Assert.Equal("GIF89a��data�", saved.ResponseBody);
        Assert.Equal("Accept: �*/*", saved.RequestHeaders);
        Assert.Equal("example.test", saved.Host);   // untouched values are unchanged
    }

    [Fact]
    public async Task ModifiedEntity_HasNulReplaced()
    {
        var capture = new CapturedRequest { Method = "GET", Host = "example.test", ResponseBody = "ok" };
        _db.Captures.Add(capture);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        capture.Notes = "binary \0 note";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var read = CreateContext();
        var saved = await read.Captures.AsNoTracking().SingleAsync(c => c.Id == capture.Id, TestContext.Current.CancellationToken);
        Assert.Equal("binary � note", saved.Notes);
    }

    [Fact]
    public async Task SynchronousSave_AlsoSanitizes()
    {
        var capture = new CapturedRequest { Method = "GET", Host = "example.test", RequestBody = "x\0y" };
        _db.Captures.Add(capture);
        _db.SaveChanges();

        await using var read = CreateContext();
        var saved = await read.Captures.AsNoTracking().SingleAsync(c => c.Id == capture.Id, TestContext.Current.CancellationToken);
        Assert.Equal("x�y", saved.RequestBody);
    }
}
