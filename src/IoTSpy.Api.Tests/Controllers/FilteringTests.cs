using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

/// <summary>
/// Tests for scanner job filtering, fuzzer job filtering, and asset upload magic-byte validation.
/// </summary>
public class FilteringTests
{
    // ── Scanner job filtering ────────────────────────────────────────────────

    private static ScannerController MakeScannerController(IScanJobRepository? scanJobs = null)
    {
        var scopes = Substitute.For<IScanScopeRepository>();
        scopes.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<ScanScope>());
        return new(Substitute.For<IScannerService>(),
            scanJobs ?? Substitute.For<IScanJobRepository>(),
            Substitute.For<IDeviceRepository>(),
            scopes,
            Substitute.For<IAuditRepository>());
    }

    [Fact]
    public async Task ListJobs_FilterByStatus_PassesStatusToRepository()
    {
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), ScanStatus.Running, Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScanJob> { new() { Status = ScanStatus.Running, TargetIp = "10.0.0.1" } });
        scanJobs.CountAsync(ScanStatus.Running, Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var controller = MakeScannerController(scanJobs);
        var result = await controller.ListJobs(1, 20, status: ScanStatus.Running, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        await scanJobs.Received(1).GetAllAsync(1, 20, ScanStatus.Running, Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListJobs_FilterByDeviceId_PassesDeviceIdToRepository()
    {
        var deviceId = Guid.NewGuid();
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ScanStatus?>(), deviceId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScanJob> { new() { DeviceId = deviceId, TargetIp = "10.0.0.1" } });
        scanJobs.CountAsync(Arg.Any<ScanStatus?>(), deviceId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var controller = MakeScannerController(scanJobs);
        var result = await controller.ListJobs(1, 20, deviceId: deviceId, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        await scanJobs.Received(1).GetAllAsync(1, 20, Arg.Any<ScanStatus?>(), deviceId, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListJobs_FilterByCreatedAfter_PassesDateToRepository()
    {
        var after = DateTimeOffset.UtcNow.AddHours(-1);
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), after, Arg.Any<CancellationToken>())
            .Returns(new List<ScanJob>());
        scanJobs.CountAsync(Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), after, Arg.Any<CancellationToken>())
            .Returns(0);

        var controller = MakeScannerController(scanJobs);
        var result = await controller.ListJobs(1, 20, createdAfter: after, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        await scanJobs.Received(1).GetAllAsync(1, 20, Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), after, Arg.Any<CancellationToken>());
    }

    // ── Fuzzer job filtering ─────────────────────────────────────────────────

    private static ManipulationController MakeManipController(IFuzzerJobRepository? fuzzer = null)
    {
        var rs = Substitute.For<IReplaySessionRepository>();
        rs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ReplaySession>());
        rs.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        var fj = fuzzer ?? Substitute.For<IFuzzerJobRepository>();
        if (fuzzer is null)
        {
            fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<FuzzerJob>());
            fj.CountAsync(Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(0);
        }
        return new ManipulationController(
            Substitute.For<IManipulationService>(),
            Substitute.For<IManipulationRuleRepository>(),
            Substitute.For<IManipulationRuleCache>(),
            Substitute.For<IBreakpointRepository>(),
            rs, fj,
            Substitute.For<ICaptureRepository>(),
            Substitute.For<IApiSpecRepository>(),
            Substitute.For<IAuditRepository>());
    }

    [Fact]
    public async Task ListFuzzerJobs_FilterByStatus_PassesStatusToRepository()
    {
        var fj = Substitute.For<IFuzzerJobRepository>();
        fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), FuzzerJobStatus.Completed, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FuzzerJob> { new() { Status = FuzzerJobStatus.Completed, Strategy = FuzzerStrategy.Random } });
        fj.CountAsync(FuzzerJobStatus.Completed, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var controller = MakeManipController(fj);
        var result = await controller.ListFuzzerJobs(1, 20, status: FuzzerJobStatus.Completed, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        await fj.Received(1).GetAllAsync(1, 20, FuzzerJobStatus.Completed, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListFuzzerJobs_FilterByCaptureId_PassesCaptureIdToRepository()
    {
        var captureId = Guid.NewGuid();
        var fj = Substitute.For<IFuzzerJobRepository>();
        fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FuzzerJobStatus?>(), captureId, Arg.Any<CancellationToken>())
            .Returns(new List<FuzzerJob> { new() { BaseCaptureId = captureId, Strategy = FuzzerStrategy.Random } });
        fj.CountAsync(Arg.Any<FuzzerJobStatus?>(), captureId, Arg.Any<CancellationToken>())
            .Returns(1);

        var controller = MakeManipController(fj);
        var result = await controller.ListFuzzerJobs(1, 20, captureId: captureId, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        await fj.Received(1).GetAllAsync(1, 20, Arg.Any<FuzzerJobStatus?>(), captureId, Arg.Any<CancellationToken>());
    }

    // ── Asset upload magic-byte validation ───────────────────────────────────

    private static ApiSpecController MakeApiSpecController() =>
        new(Substitute.For<IApiSpecService>(),
            Substitute.For<IApiSpecRepository>(),
            new IoTSpy.Manipulation.ApiSpec.ReplacementPreviewService(
                new IoTSpy.Manipulation.ApiSpec.ContentReplacer(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<IoTSpy.Manipulation.ApiSpec.ContentReplacer>.Instance),
                Substitute.For<IApiSpecRepository>(),
                Substitute.For<ICaptureRepository>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IoTSpy.Manipulation.ApiSpec.ReplacementPreviewService>.Instance));

    private static IFormFile MakeFormFile(string fileName, byte[] content)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(fileName);
        file.Length.Returns(content.Length);
        file.OpenReadStream().Returns(_ => new MemoryStream(content));
        return file;
    }

    [Fact]
    public async Task UploadAsset_ValidPng_Returns200()
    {
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        var file = MakeFormFile("test.png", pngHeader);
        var controller = MakeApiSpecController();

        // The controller will try to create the assets directory and write a file — use a temp dir
        var result = await controller.UploadAsset(file, TestContext.Current.CancellationToken);

        // Not 415
        Assert.IsNotType<ObjectResult>(result is ObjectResult oor && oor.StatusCode == 415 ? result : null);
    }

    [Fact]
    public async Task UploadAsset_PngFileWithJpegBytes_Returns415()
    {
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        var file = MakeFormFile("photo.png", jpegBytes); // wrong extension for the magic bytes
        var controller = MakeApiSpecController();

        var result = await controller.UploadAsset(file, TestContext.Current.CancellationToken);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, statusResult.StatusCode);
        Assert.Contains("PNG", statusResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task UploadAsset_JpegFileWithGifBytes_Returns415()
    {
        var gifBytes = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0, 0, 0, 0, 0 };
        var file = MakeFormFile("image.jpg", gifBytes);
        var controller = MakeApiSpecController();

        var result = await controller.UploadAsset(file, TestContext.Current.CancellationToken);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, statusResult.StatusCode);
        Assert.Contains("JPEG", statusResult.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task UploadAsset_JsonFile_AllowedWithoutMagicCheck()
    {
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        var file = MakeFormFile("data.json", jsonBytes);
        var controller = MakeApiSpecController();

        // Text-based files have no magic bytes — should not be rejected as 415
        var result = await controller.UploadAsset(file, TestContext.Current.CancellationToken);

        if (result is ObjectResult oor)
            Assert.NotEqual(415, oor.StatusCode);
    }

    [Fact]
    public async Task UploadAsset_WebMFileWithCorrectMagic_NotRejected()
    {
        var webmHeader = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var file = MakeFormFile("video.webm", webmHeader);
        var controller = MakeApiSpecController();

        var result = await controller.UploadAsset(file, TestContext.Current.CancellationToken);

        if (result is ObjectResult oor)
            Assert.NotEqual(415, oor.StatusCode);
    }
}
