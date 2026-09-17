using IoTSpy.Api.Controllers;
using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

// Backup/restore need a real on-disk SQLite file (VACUUM INTO and file-swap
// don't behave meaningfully against the ":memory:"/shared-cache connection
// string used by the ASP.NET integration test harness), so these tests build
// their own file-backed IoTSpyDbContext rather than reusing that harness.
public class AdminBackupRestoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"iotspy-test-{Guid.NewGuid():N}.db");
    private readonly List<string> _extraFilesToClean = [];

    private IoTSpyDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<IoTSpyDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var db = new IoTSpyDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private AdminController CreateController(IoTSpyDbContext db, IAuditRepository? audit = null)
    {
        var retentionSettings = new DataRetentionSettingsService(Options.Create(new DataRetentionOptions()));
        var controller = new AdminController(db, audit ?? Substitute.For<IAuditRepository>(), retentionSettings);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        return controller;
    }

    private static IFormFile MakeUploadedFile(byte[] bytes, string fileName = "upload.db")
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.LongLength, "file", fileName);
    }

    [Fact]
    public async Task BackupDatabase_ReturnsValidSqliteFile()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.BackupDatabase(TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("application/octet-stream", result.ContentType);
        var header = System.Text.Encoding.ASCII.GetString(result.FileContents, 0, 16);
        Assert.Equal("SQLite format 3\0", header);
    }

    [Fact]
    public async Task RestoreDatabase_NonSqliteFile_ReturnsBadRequest()
    {
        using var db = CreateDb();
        var controller = CreateController(db);
        var badFile = MakeUploadedFile("not a sqlite database"u8.ToArray());

        var result = await controller.RestoreDatabase(badFile, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RestoreDatabase_NoFileUploaded_ReturnsBadRequest()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        var result = await controller.RestoreDatabase(null!, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BackupThenRestore_RoundTrip_RestoresPriorState()
    {
        using var db = CreateDb();
        var controller = CreateController(db);

        db.Devices.Add(new Device { IpAddress = "10.0.0.1", Label = "before-backup" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Snapshot the DB while it has exactly one device.
        var backupResult = await controller.BackupDatabase(TestContext.Current.CancellationToken) as FileContentResult;
        var backupBytes = backupResult!.FileContents;

        // Mutate the live DB after the snapshot was taken.
        db.Devices.Add(new Device { IpAddress = "10.0.0.2", Label = "after-backup" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await db.Devices.CountAsync(TestContext.Current.CancellationToken));

        // Restore from the snapshot — should discard the post-backup mutation.
        var uploadedFile = MakeUploadedFile(backupBytes);
        var restoreResult = await controller.RestoreDatabase(uploadedFile, TestContext.Current.CancellationToken);
        Assert.IsType<OkObjectResult>(restoreResult);

        // A fresh context against the (now-restored) file reflects the pre-mutation state;
        // SqliteConnection.ClearAllPools() inside RestoreDatabase means even the same
        // connection string now reads the restored file.
        using var dbAfterRestore = CreateDb();
        var devices = await dbAfterRestore.Devices.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(devices);
        Assert.Equal("before-backup", devices[0].Label);
    }

    [Fact]
    public async Task RestoreDatabase_ValidFile_WritesAuditEntry()
    {
        using var db = CreateDb();
        var audit = Substitute.For<IAuditRepository>();
        var controller = CreateController(db, audit);

        var backupResult = await controller.BackupDatabase(TestContext.Current.CancellationToken) as FileContentResult;
        var uploadedFile = MakeUploadedFile(backupResult!.FileContents);

        await controller.RestoreDatabase(uploadedFile, TestContext.Current.CancellationToken);

        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "DatabaseRestore" && e.EntityType == "Database"),
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        // Scoped to this test's own connection string — ClearAllPools() is process-wide
        // and tears down every other SQLite connection in the process, including other
        // test fixtures' in-memory-mode databases running in parallel.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), "iotspy*pre-restore*.db"))
                File.Delete(f);
        }
        catch
        {
            // Best-effort cleanup; leftover temp files don't fail the test run.
        }
    }
}
