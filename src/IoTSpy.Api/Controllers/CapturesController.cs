using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Core.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/captures")]
public class CapturesController(ICaptureRepository captures) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? deviceId,
        [FromQuery] string? host,
        [FromQuery] string? method,
        [FromQuery] int? statusCode,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? q,
        [FromQuery] string? clientIp,
        [FromQuery] string? headerQ,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var filter = new CaptureFilter(deviceId, host, method, statusCode, from, to, q, clientIp, headerQ);
        var rawItems = await captures.GetPagedAsync(filter, page, pageSize);
        var total = await captures.CountAsync(filter);
        var items = rawItems.Select(c => new
        {
            c.Id,
            c.DeviceId,
            c.Method,
            c.Scheme,
            c.Host,
            c.Port,
            c.Path,
            c.Query,
            c.RequestHeaders,
            c.RequestBodySize,
            c.StatusCode,
            c.StatusMessage,
            c.ResponseHeaders,
            c.ResponseBodySize,
            c.IsTls,
            c.TlsVersion,
            c.TlsCipherSuite,
            c.Protocol,
            c.Timestamp,
            c.DurationMs,
            c.ClientIp,
            c.IsModified,
            c.Notes,
        }).ToList();
        return Ok(new { items, total, page, pageSize, pages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var capture = await captures.GetByIdAsync(id);
        return capture is null ? NotFound() : Ok(capture);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await captures.DeleteAsync(id);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Clear(
        [FromQuery] Guid? deviceId,
        [FromQuery] string? host,
        [FromQuery] string? method,
        [FromQuery] int? statusCode,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? clientIp,
        CancellationToken ct)
    {
        if (host is not null || method is not null || statusCode.HasValue || from.HasValue || to.HasValue || clientIp is not null)
        {
            var filter = new CaptureFilter(deviceId, host, method, statusCode, from, to, null, clientIp);
            await captures.ClearByFilterAsync(filter, ct);
        }
        else
        {
            await captures.ClearAsync(deviceId, ct);
        }
        return NoContent();
    }

    // ── Export endpoints (Phase 9.2) ─────────────────────────────────────────

    [HttpGet("export/csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] Guid? deviceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var items = await GetExportItems(deviceId, from, to, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Id,Timestamp,Method,Url,StatusCode,RequestSize,ResponseSize,IsModified,Protocol");
        foreach (var r in items)
        {
            var url = $"{r.Scheme}://{r.Host}{r.Path}{r.Query}";
            sb.AppendLine($"{r.Id},{r.Timestamp:O},{CsvEscape(r.Method)},{CsvEscape(url)},{r.StatusCode},{r.RequestBodySize},{r.ResponseBodySize},{r.IsModified},{r.Protocol}");
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "captures.csv");
    }

    [HttpGet("export/json")]
    public async Task<IActionResult> ExportJson(
        [FromQuery] Guid? deviceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var items = await GetExportItems(deviceId, from, to, ct);
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", "captures.json");
    }

    [HttpGet("export/har")]
    public async Task<IActionResult> ExportHar(
        [FromQuery] Guid? deviceId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var items = await GetExportItems(deviceId, from, to, ct);
        var har = BuildHar(items);
        var json = JsonSerializer.Serialize(har, new JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", "captures.har");
    }

    private Task<List<CapturedRequest>> GetExportItems(
        Guid? deviceId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var filter = new CaptureFilter(deviceId, null, null, null, from, to, null);
        return captures.GetPagedAsync(filter, 1, 10_000, ct);
    }

    private static object BuildHar(List<CapturedRequest> items) => new
    {
        log = new
        {
            version = "1.2",
            creator = new { name = "IoTSpy", version = "1.0" },
            entries = items.Select(r => new
            {
                startedDateTime = r.Timestamp.ToString("O"),
                time = r.DurationMs,
                request = new
                {
                    method = r.Method,
                    url = $"{r.Scheme}://{r.Host}{r.Path}{r.Query}",
                    httpVersion = "HTTP/1.1",
                    headers = HttpHeaderParser.ParseHeaderLines(r.RequestHeaders)
                        .Select(h => new { name = h.Name, value = h.Value }),
                    queryString = Array.Empty<object>(),
                    cookies = Array.Empty<object>(),
                    headersSize = -1,
                    bodySize = r.RequestBodySize
                },
                response = new
                {
                    status = r.StatusCode,
                    statusText = r.StatusMessage,
                    httpVersion = "HTTP/1.1",
                    headers = HttpHeaderParser.ParseHeaderLines(r.ResponseHeaders)
                        .Select(h => new { name = h.Name, value = h.Value }),
                    cookies = Array.Empty<object>(),
                    content = new { size = r.ResponseBodySize, mimeType = "application/octet-stream" },
                    redirectURL = "",
                    headersSize = -1,
                    bodySize = r.ResponseBodySize
                },
                cache = new { },
                timings = new { send = 0, wait = r.DurationMs, receive = 0 }
            })
        }
    };

    // ── Streaming asset export (Phase 23.1) ──────────────────────────────────

    [HttpPost("{id:guid}/export-as-asset")]
    public async Task<IActionResult> ExportAsAsset(Guid id, CancellationToken ct)
    {
        var (capture, responseBody, contentType, ext, error) = await ResolveStreamingCapture(id, ct);
        if (error is not null) return error;

        var filename = BuildAssetFilename(capture!.Host, capture.Path, ext!);
        var assetsDir = AssetsPaths.AssetsDirectory;
        Directory.CreateDirectory(assetsDir);
        var filePath = Path.Combine(assetsDir, filename);
        await System.IO.File.WriteAllTextAsync(filePath, responseBody, Encoding.UTF8, ct);

        return Ok(new ExportCaptureAsAssetResult(filename, filePath, contentType!, Encoding.UTF8.GetByteCount(responseBody!)));
    }

    [HttpGet("{id:guid}/download-body")]
    public async Task<IActionResult> DownloadBody(Guid id, CancellationToken ct)
    {
        var (capture, responseBody, contentType, ext, error) = await ResolveStreamingCapture(id, ct);
        if (error is not null) return error;

        var filename = BuildAssetFilename(capture!.Host, capture.Path, ext!);
        return File(Encoding.UTF8.GetBytes(responseBody!), contentType!, filename);
    }

    private async Task<(CapturedRequest? capture, string? body, string? contentType, string? ext, IActionResult? error)>
        ResolveStreamingCapture(Guid id, CancellationToken ct)
    {
        var capture = await captures.GetByIdAsync(id, ct);
        if (capture is null)
            return (null, null, null, null, NotFound());

        var body = capture.ResponseBody;
        if (string.IsNullOrEmpty(body) || body.StartsWith("b64:", StringComparison.Ordinal))
            return (null, null, null, null, UnprocessableEntity("Response body is absent or binary"));

        var contentType = ParseContentType(capture.ResponseHeaders);
        var ext = MapStreamingExtension(contentType);
        if (ext is null)
            return (null, null, null, null, UnprocessableEntity("Content-Type is not a supported streaming type"));

        return (capture, body, contentType, ext, null);
    }

    private static string? ParseContentType(string? headers) =>
        HttpHeaderParser.FindHeaderValue(headers, "Content-Type");

    private static string? MapStreamingExtension(string? contentType)
    {
        if (contentType is null) return null;
        var ct = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (ct == "text/event-stream") return ".sse";
        if (ct is "application/x-ndjson" or "application/json-stream" or "application/jsonlines") return ".ndjson";
        return null;
    }

    private static string BuildAssetFilename(string host, string path, string ext)
    {
        static string Sanitize(string s) =>
            Regex.Replace(s, @"[^a-zA-Z0-9_\-]", "_").Trim('_')[..Math.Min(32, Regex.Replace(s, @"[^a-zA-Z0-9_\-]", "_").Trim('_').Length)];

        var h = Sanitize(host);
        var p = Sanitize(path.Trim('/'));
        return $"{h}_{p}_{Guid.NewGuid():N}{ext}";
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public record ExportCaptureAsAssetResult(string FileName, string FilePath, string ContentType, long SizeBytes);

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
