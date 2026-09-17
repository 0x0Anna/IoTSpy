using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class CapturesControllerTests
{
    private static CapturedRequest MakeCapture(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Host = "example.com",
        Method = "GET",
        Path = "/api/test",
        StatusCode = 200,
        Timestamp = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task List_ReturnsPagedResults()
    {
        var repo = Substitute.For<ICaptureRepository>();
        var captures = new List<CapturedRequest> { MakeCapture(), MakeCapture() };
        repo.GetPagedAsync(Arg.Any<CaptureFilter>(), 1, 50, Arg.Any<CancellationToken>()).Returns(captures);
        repo.CountAsync(Arg.Any<CaptureFilter>(), Arg.Any<CancellationToken>()).Returns(2);

        var controller = new CapturesController(repo);
        var result = await controller.List(null, null, null, null, null, null, null, null, null) as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":2", json);
    }

    [Fact]
    public async Task Get_WhenFound_ReturnsCapture()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeCapture(id));

        var controller = new CapturesController(repo);
        var result = await controller.Get(id) as OkObjectResult;

        Assert.NotNull(result);
        Assert.IsType<CapturedRequest>(result.Value);
    }

    [Fact]
    public async Task Get_WhenNotFound_ReturnsNotFound()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);

        var controller = new CapturesController(repo);
        var result = await controller.Get(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_CallsRepoDeleteAndReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<ICaptureRepository>();
        repo.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var controller = new CapturesController(repo);
        var result = await controller.Delete(id);

        Assert.IsType<NoContentResult>(result);
        await repo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_CallsRepoClearAndReturnsNoContent()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.ClearAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var controller = new CapturesController(repo);
        var result = await controller.Clear(null, null, null, null, null, null, null, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await repo.Received(1).ClearAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_PageSizeClampedTo200()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetPagedAsync(Arg.Any<CaptureFilter>(), 1, 200, Arg.Any<CancellationToken>()).Returns(new List<CapturedRequest>());
        repo.CountAsync(Arg.Any<CaptureFilter>(), Arg.Any<CancellationToken>()).Returns(0);

        var controller = new CapturesController(repo);
        await controller.List(null, null, null, null, null, null, null, null, null, pageSize: 9999);

        await repo.Received(1).GetPagedAsync(Arg.Any<CaptureFilter>(), 1, 200, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ab", null)]
    [InlineData(null, "ab")]
    public async Task List_ShortSearchTerm_ReturnsBadRequest(string? q, string? headerQ)
    {
        var repo = Substitute.For<ICaptureRepository>();
        var controller = new CapturesController(repo);

        var result = await controller.List(null, null, null, null, null, null, q, null, headerQ);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceive().GetPagedAsync(Arg.Any<CaptureFilter>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ThreeCharSearchTerm_IsAllowed()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetPagedAsync(Arg.Any<CaptureFilter>(), 1, 50, Arg.Any<CancellationToken>()).Returns(new List<CapturedRequest>());
        repo.CountAsync(Arg.Any<CaptureFilter>(), Arg.Any<CancellationToken>()).Returns(0);
        var controller = new CapturesController(repo);

        var result = await controller.List(null, null, null, null, null, null, "abc", null, null);

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Capture-to-curl ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAsCurl_WhenNotFound_ReturnsNotFound()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);
        var controller = new CapturesController(repo);

        var result = await controller.ExportAsCurl(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportAsCurl_BuildsCommandWithMethodHeadersAndBody()
    {
        var capture = MakeCapture();
        capture.Scheme = "https";
        capture.Method = "POST";
        capture.Host = "example.com";
        capture.Port = 443;
        capture.Path = "/api/widgets";
        capture.Query = "?id=1";
        capture.RequestHeaders = "Content-Type: application/json\r\nAuthorization: Bearer it's-a-token\r\n";
        capture.RequestBody = "{\"name\":\"widget\"}";

        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);
        var controller = new CapturesController(repo);

        var result = await controller.ExportAsCurl(capture.Id, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var curl = (string)result.Value!.GetType().GetProperty("curl")!.GetValue(result.Value)!;
        Assert.Contains("curl -X POST", curl);
        Assert.Contains("'https://example.com/api/widgets?id=1'", curl); // default TLS port omitted
        Assert.Contains("-H 'Content-Type: application/json'", curl);
        Assert.Contains("-H 'Authorization: Bearer it'\\''s-a-token'", curl); // shell-escaped single quote
        Assert.Contains("--data-raw '{\"name\":\"widget\"}'", curl);
    }

    [Fact]
    public async Task ExportAsCurl_NonDefaultPort_IsIncludedInUrl()
    {
        var capture = MakeCapture();
        capture.Scheme = "https";
        capture.Port = 8443;

        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(capture.Id, Arg.Any<CancellationToken>()).Returns(capture);
        var controller = new CapturesController(repo);

        var result = await controller.ExportAsCurl(capture.Id, TestContext.Current.CancellationToken) as OkObjectResult;

        var curl = (string)result!.Value!.GetType().GetProperty("curl")!.GetValue(result.Value)!;
        Assert.Contains(":8443", curl);
    }

    // ── Capture diff ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_SameId_ReturnsBadRequest()
    {
        var id = Guid.NewGuid();
        var repo = Substitute.For<ICaptureRepository>();
        var controller = new CapturesController(repo);

        var result = await controller.Diff(id, id, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Diff_EitherCaptureMissing_ReturnsNotFoundNamingMissingIds()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(a, Arg.Any<CancellationToken>()).Returns(MakeCapture(a));
        repo.GetByIdAsync(b, Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);
        var controller = new CapturesController(repo);

        var result = await controller.Diff(a, b, TestContext.Current.CancellationToken) as NotFoundObjectResult;

        Assert.NotNull(result);
        var missing = (List<Guid>)result.Value!.GetType().GetProperty("missing")!.GetValue(result.Value)!;
        Assert.Equal([b], missing);
    }

    [Fact]
    public async Task Diff_IdenticalCaptures_ReportsNoChanges()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var captureA = MakeCapture(idA);
        captureA.RequestHeaders = "X-Test: value\r\n";
        var captureB = MakeCapture(idB);
        captureB.Method = captureA.Method;
        captureB.Scheme = captureA.Scheme;
        captureB.Host = captureA.Host;
        captureB.Path = captureA.Path;
        captureB.Query = captureA.Query;
        captureB.StatusCode = captureA.StatusCode;
        captureB.RequestHeaders = captureA.RequestHeaders;
        captureB.RequestBody = captureA.RequestBody;
        captureB.ResponseBody = captureA.ResponseBody;

        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(idA, Arg.Any<CancellationToken>()).Returns(captureA);
        repo.GetByIdAsync(idB, Arg.Any<CancellationToken>()).Returns(captureB);
        var controller = new CapturesController(repo);

        var result = await controller.Diff(idA, idB, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var diff = Assert.IsType<CapturesController.CaptureDiffResult>(result.Value);
        Assert.False(diff.MethodChanged);
        Assert.False(diff.UrlChanged);
        Assert.False(diff.StatusCodeChanged);
        Assert.Empty(diff.RequestHeaderDiff);
        Assert.True(diff.RequestBodyEqual);
        Assert.True(diff.ResponseBodyEqual);
    }

    [Fact]
    public async Task Diff_DifferingCaptures_ReportsChangesAndHeaderDiff()
    {
        var captureA = MakeCapture();
        captureA.Method = "GET";
        captureA.StatusCode = 200;
        captureA.RequestHeaders = "Authorization: token-a\r\nOnly-In-A: yes\r\n";
        captureA.RequestBody = "a-body";

        var captureB = MakeCapture();
        captureB.Method = "POST";
        captureB.StatusCode = 500;
        captureB.RequestHeaders = "Authorization: token-b\r\nOnly-In-B: yes\r\n";
        captureB.RequestBody = "b-body";

        var repo = Substitute.For<ICaptureRepository>();
        repo.GetByIdAsync(captureA.Id, Arg.Any<CancellationToken>()).Returns(captureA);
        repo.GetByIdAsync(captureB.Id, Arg.Any<CancellationToken>()).Returns(captureB);
        var controller = new CapturesController(repo);

        var result = await controller.Diff(captureA.Id, captureB.Id, TestContext.Current.CancellationToken) as OkObjectResult;

        var diff = Assert.IsType<CapturesController.CaptureDiffResult>(result!.Value);
        Assert.True(diff.MethodChanged);
        Assert.True(diff.StatusCodeChanged);
        Assert.False(diff.RequestBodyEqual);
        Assert.Equal(3, diff.RequestHeaderDiff.Count); // Authorization changed, Only-In-A, Only-In-B
        Assert.Contains(diff.RequestHeaderDiff, h => h.Name == "Authorization" && h.ValueA == "token-a" && h.ValueB == "token-b");
        Assert.Contains(diff.RequestHeaderDiff, h => h.Name == "Only-In-A" && h.ValueA == "yes" && h.ValueB == null);
        Assert.Contains(diff.RequestHeaderDiff, h => h.Name == "Only-In-B" && h.ValueA == null && h.ValueB == "yes");
    }
}
