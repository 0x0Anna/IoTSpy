using System.Text.Json;
using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class CaptureImportTests
{
    private static JsonElement ParseHar(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task ImportHar_ValidHar_ImportsAllEntries()
    {
        var har = ParseHar("""
        {
          "log": {
            "version": "1.2",
            "entries": [
              {
                "startedDateTime": "2026-05-14T00:00:00.000Z",
                "time": 42,
                "request": {
                  "method": "GET",
                  "url": "https://example.com:8443/api/widgets?id=1",
                  "headers": [ { "name": "Authorization", "value": "Bearer abc" } ]
                },
                "response": {
                  "status": 200,
                  "statusText": "OK",
                  "headers": [ { "name": "Content-Type", "value": "application/json" } ],
                  "content": { "size": 2, "mimeType": "application/json", "text": "{}" }
                }
              }
            ]
          }
        }
        """);

        var repo = Substitute.For<ICaptureRepository>();
        List<CapturedRequest>? captured = null;
        await repo.AddBatchAsync(Arg.Do<IReadOnlyList<CapturedRequest>>(c => captured = c.ToList()), Arg.Any<CancellationToken>());

        var controller = new CapturesController(repo);
        var result = await controller.ImportHar(har, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"imported\":1", json);
        Assert.Contains("\"skipped\":0", json);

        Assert.NotNull(captured);
        var c = Assert.Single(captured!);
        Assert.Equal("GET", c.Method);
        Assert.Equal("https", c.Scheme);
        Assert.Equal("example.com", c.Host);
        Assert.Equal(8443, c.Port);
        Assert.Equal("/api/widgets", c.Path);
        Assert.Equal("?id=1", c.Query);
        Assert.Equal(200, c.StatusCode);
        Assert.Contains("Authorization: Bearer abc", c.RequestHeaders);
        Assert.Contains("Content-Type: application/json", c.ResponseHeaders);
        Assert.True(c.IsTls);
    }

    [Fact]
    public async Task ImportHar_MissingLogEntries_ReturnsBadRequest()
    {
        var repo = Substitute.For<ICaptureRepository>();
        var controller = new CapturesController(repo);

        var result = await controller.ImportHar(ParseHar("{}"), TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImportHar_OneBadEntryAmongGoodOnes_ImportsGoodAndSkipsBad()
    {
        var har = ParseHar("""
        {
          "log": {
            "entries": [
              {
                "startedDateTime": "2026-05-14T00:00:00.000Z",
                "time": 10,
                "request": { "method": "GET", "url": "https://good.example.com/", "headers": [] },
                "response": { "status": 200, "headers": [] }
              },
              {
                "startedDateTime": "2026-05-14T00:00:00.000Z",
                "time": 10,
                "request": { "method": "GET", "url": "not a valid url", "headers": [] },
                "response": { "status": 200, "headers": [] }
              }
            ]
          }
        }
        """);

        var repo = Substitute.For<ICaptureRepository>();
        var controller = new CapturesController(repo);

        var result = await controller.ImportHar(har, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"imported\":1", json);
        Assert.Contains("\"skipped\":1", json);
    }

    [Fact]
    public async Task ImportHar_RoundTripFromExportedHar_PreservesMethodUrlAndStatus()
    {
        var original = new CapturedRequest
        {
            Method = "POST",
            Scheme = "https",
            Host = "roundtrip.example.com",
            Port = 443,
            Path = "/api/echo",
            Query = "?x=1",
            RequestHeaders = "X-Test: value\r\n",
            StatusCode = 201,
            StatusMessage = "Created",
            ResponseHeaders = "Content-Type: application/json\r\n",
            Timestamp = DateTimeOffset.UtcNow,
            DurationMs = 5
        };

        var exportRepo = Substitute.For<ICaptureRepository>();
        exportRepo.GetPagedAsync(Arg.Any<CaptureFilter>(), 1, 10_000, Arg.Any<CancellationToken>())
            .Returns(new List<CapturedRequest> { original });
        var exportController = new CapturesController(exportRepo);
        var exportResult = await exportController.ExportHar(null, null, null, TestContext.Current.CancellationToken) as FileContentResult;
        var harJson = System.Text.Encoding.UTF8.GetString(exportResult!.FileContents);
        var har = ParseHar(harJson);

        var importRepo = Substitute.For<ICaptureRepository>();
        List<CapturedRequest>? imported = null;
        await importRepo.AddBatchAsync(Arg.Do<IReadOnlyList<CapturedRequest>>(c => imported = c.ToList()), Arg.Any<CancellationToken>());
        var importController = new CapturesController(importRepo);
        await importController.ImportHar(har, TestContext.Current.CancellationToken);

        var reimported = Assert.Single(imported!);
        Assert.Equal(original.Method, reimported.Method);
        Assert.Equal(original.Scheme, reimported.Scheme);
        Assert.Equal(original.Host, reimported.Host);
        Assert.Equal(original.Path, reimported.Path);
        Assert.Equal(original.Query, reimported.Query);
        Assert.Equal(original.StatusCode, reimported.StatusCode);
    }
}
