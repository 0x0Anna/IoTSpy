using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Text;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class CaptureStreamingAssetTests : IDisposable
{
    private readonly ICaptureRepository _repo = Substitute.For<ICaptureRepository>();
    private readonly CapturesController _controller;
    private readonly string _tempDir;

    public CaptureStreamingAssetTests()
    {
        _controller = new CapturesController(_repo);

        // Redirect assets directory to a temp folder so tests don't write to real locations
        _tempDir = Path.Combine(Path.GetTempPath(), $"iotspy_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        // Override AppContext.BaseDirectory indirectly by patching is not feasible;
        // ExportAsAsset writes to AssetsPaths.AssetsDirectory which uses AppContext.BaseDirectory.
        // For the DownloadBody test we only check the response — no filesystem write occurs.
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Capture headers are stored as raw "Name: Value\r\n..." HTTP header text
    // (as written by the proxy pipeline), not JSON.
    private static string SseHeaders() => "Content-Type: text/event-stream\r\n";

    private static string NdjsonHeaders() => "Content-Type: application/x-ndjson\r\n";

    private static string JsonHeaders() => "Content-Type: application/json\r\n";

    private static CapturedRequest MakeSseCapture(string? body = "data: hello\n\n", string? headers = null) => new()
    {
        Id = Guid.NewGuid(),
        Host = "api.example.com",
        Path = "/events/stream",
        ResponseHeaders = headers ?? SseHeaders(),
        ResponseBody = body ?? string.Empty,
        ResponseBodySize = body?.Length ?? 0,
    };

    // ── DownloadBody ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadBody_SseCapture_ReturnsFileWithCorrectContentType()
    {
        var capture = MakeSseCapture();
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.DownloadBody(capture.Id, TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("text/event-stream", result.ContentType);
        Assert.Contains(".sse", result.FileDownloadName);
        Assert.Equal(capture.ResponseBody, Encoding.UTF8.GetString(result.FileContents));
    }

    [Fact]
    public async Task DownloadBody_NdjsonCapture_ReturnsDotNdjsonExtension()
    {
        var capture = new CapturedRequest
        {
            Id = Guid.NewGuid(),
            Host = "api.example.com",
            Path = "/stream",
            ResponseHeaders = NdjsonHeaders(),
            ResponseBody = "{\"x\":1}\n{\"x\":2}\n",
            ResponseBodySize = 20,
        };
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.DownloadBody(capture.Id, TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("application/x-ndjson", result.ContentType);
        Assert.Contains(".ndjson", result.FileDownloadName);
    }

    [Fact]
    public async Task DownloadBody_UnknownId_Returns404()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);

        var result = await _controller.DownloadBody(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadBody_BinaryBody_Returns422()
    {
        var capture = MakeSseCapture(body: "b64:AAEC");
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.DownloadBody(capture.Id, TestContext.Current.CancellationToken);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task DownloadBody_NullBody_Returns422()
    {
        var capture = MakeSseCapture(body: null);
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.DownloadBody(capture.Id, TestContext.Current.CancellationToken);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    [Fact]
    public async Task DownloadBody_NonStreamingContentType_Returns422()
    {
        var capture = new CapturedRequest
        {
            Id = Guid.NewGuid(),
            Host = "api.example.com",
            Path = "/data",
            ResponseHeaders = JsonHeaders(),
            ResponseBody = "{\"a\":1}",
            ResponseBodySize = 7,
        };
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.DownloadBody(capture.Id, TestContext.Current.CancellationToken);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }

    // ── ExportAsAsset ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAsAsset_SseCapture_WritesFileAndReturnsResult()
    {
        var capture = MakeSseCapture();
        _repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);

        var result = await _controller.ExportAsAsset(capture.Id, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var dto = Assert.IsType<CapturesController.ExportCaptureAsAssetResult>(result.Value);
        Assert.Contains(".sse", dto.FileName);
        Assert.Equal("text/event-stream", dto.ContentType);
        Assert.True(dto.SizeBytes > 0);
        Assert.True(File.Exists(dto.FilePath));
        Assert.Equal(capture.ResponseBody, await File.ReadAllTextAsync(dto.FilePath, TestContext.Current.CancellationToken));

        // cleanup
        File.Delete(dto.FilePath);
    }

    [Fact]
    public async Task ExportAsAsset_UnknownId_Returns404()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);

        var result = await _controller.ExportAsAsset(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }
}
