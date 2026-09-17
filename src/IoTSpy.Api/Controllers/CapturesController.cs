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
    // A 1-2 character LIKE '%x%' term is a near-full-table scan on every row
    // regardless of DB provider (a leading wildcard defeats a B-tree index on
    // both SQLite and Postgres), so reject short search terms rather than let
    // them silently degrade query performance.
    private const int MinSearchTermLength = 3;

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
        if (q is { Length: > 0 and < MinSearchTermLength } || headerQ is { Length: > 0 and < MinSearchTermLength })
            return BadRequest(new { error = $"Search terms must be at least {MinSearchTermLength} characters" });

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

    [HttpPost("import/har")]
    public async Task<IActionResult> ImportHar([FromBody] JsonElement har, CancellationToken ct)
    {
        if (!har.TryGetProperty("log", out var log) || !log.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return BadRequest(new { error = "Invalid HAR: expected log.entries array" });

        var toImport = new List<CapturedRequest>();
        var skipped = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            var capture = TryParseHarEntry(entry);
            if (capture is null) skipped++;
            else toImport.Add(capture);
        }

        if (toImport.Count > 0)
            await captures.AddBatchAsync(toImport, ct);

        return Ok(new { imported = toImport.Count, skipped });
    }

    private static CapturedRequest? TryParseHarEntry(JsonElement entry)
    {
        try
        {
            var request = entry.GetProperty("request");
            var response = entry.GetProperty("response");
            var uri = new Uri(request.GetProperty("url").GetString()!);

            var requestBody = request.TryGetProperty("postData", out var postData) &&
                               postData.TryGetProperty("text", out var textEl)
                ? textEl.GetString() ?? ""
                : "";

            var responseBody = response.TryGetProperty("content", out var content) &&
                                content.TryGetProperty("text", out var respTextEl)
                ? respTextEl.GetString() ?? ""
                : "";

            return new CapturedRequest
            {
                Method = request.GetProperty("method").GetString() ?? "GET",
                Scheme = uri.Scheme,
                Host = uri.Host,
                Port = uri.Port,
                Path = uri.AbsolutePath,
                Query = uri.Query,
                RequestHeaders = BuildRawHeaders(request),
                RequestBody = requestBody,
                RequestBodySize = Encoding.UTF8.GetByteCount(requestBody),
                StatusCode = response.GetProperty("status").GetInt32(),
                StatusMessage = response.TryGetProperty("statusText", out var st) ? st.GetString() ?? "" : "",
                ResponseHeaders = BuildRawHeaders(response),
                ResponseBody = responseBody,
                ResponseBodySize = Encoding.UTF8.GetByteCount(responseBody),
                IsTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                Protocol = IoTSpy.Core.Enums.InterceptionProtocol.Http,
                Timestamp = entry.TryGetProperty("startedDateTime", out var started) && started.GetString() is { } s
                    ? DateTimeOffset.Parse(s)
                    : DateTimeOffset.UtcNow,
                DurationMs = entry.TryGetProperty("time", out var time) ? (long)time.GetDouble() : 0,
                Notes = "Imported from HAR"
            };
        }
        catch
        {
            return null;
        }
    }

    private static string BuildRawHeaders(JsonElement requestOrResponse)
    {
        if (!requestOrResponse.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        return sb.ToString();
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

    // ── Capture-to-curl ───────────────────────────────────────────────────────

    [HttpGet("{id:guid}/curl")]
    public async Task<IActionResult> ExportAsCurl(Guid id, CancellationToken ct)
    {
        var capture = await captures.GetByIdAsync(id, ct);
        if (capture is null) return NotFound();

        return Ok(new { curl = BuildCurlCommand(capture) });
    }

    private static string BuildCurlCommand(CapturedRequest r)
    {
        var parts = new List<string> { $"curl -X {r.Method}", $"'{BuildCurlUrl(r)}'" };

        foreach (var (name, value) in HttpHeaderParser.ParseHeaderLines(r.RequestHeaders))
            parts.Add($"-H '{ShellQuote(name)}: {ShellQuote(value)}'");

        if (!string.IsNullOrEmpty(r.RequestBody))
            parts.Add($"--data-raw '{ShellQuote(r.RequestBody)}'");

        return string.Join(" \\\n  ", parts);
    }

    private static string BuildCurlUrl(CapturedRequest r)
    {
        var isDefaultPort = r.Port <= 0
            || (r.Port == 80 && r.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            || (r.Port == 443 && r.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
        var portSuffix = isDefaultPort ? "" : $":{r.Port}";
        return $"{r.Scheme}://{r.Host}{portSuffix}{r.Path}{r.Query}";
    }

    private static string ShellQuote(string value) => value.Replace("'", "'\\''");

    // ── Capture diff ──────────────────────────────────────────────────────────

    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] Guid a, [FromQuery] Guid b, CancellationToken ct)
    {
        if (a == b)
            return BadRequest(new { error = "Cannot diff a capture against itself" });

        var captureA = await captures.GetByIdAsync(a, ct);
        var captureB = await captures.GetByIdAsync(b, ct);

        var missing = new List<Guid>();
        if (captureA is null) missing.Add(a);
        if (captureB is null) missing.Add(b);
        if (missing.Count > 0)
            return NotFound(new { error = "Capture not found", missing });

        return Ok(BuildDiff(captureA!, captureB!));
    }

    private static CaptureDiffResult BuildDiff(CapturedRequest a, CapturedRequest b)
    {
        var urlA = BuildCurlUrl(a);
        var urlB = BuildCurlUrl(b);

        return new CaptureDiffResult(
            new CaptureDiffSummary(a.Id, a.Method, urlA, a.StatusCode, a.Timestamp),
            new CaptureDiffSummary(b.Id, b.Method, urlB, b.StatusCode, b.Timestamp),
            MethodChanged: a.Method != b.Method,
            UrlChanged: urlA != urlB,
            StatusCodeChanged: a.StatusCode != b.StatusCode,
            RequestHeaderDiff: DiffHeaders(a.RequestHeaders, b.RequestHeaders),
            ResponseHeaderDiff: DiffHeaders(a.ResponseHeaders, b.ResponseHeaders),
            RequestBodyEqual: a.RequestBody == b.RequestBody,
            ResponseBodyEqual: a.ResponseBody == b.ResponseBody);
    }

    private static List<HeaderDiffEntry> DiffHeaders(string? headersA, string? headersB)
    {
        // Last value wins on a duplicate header name — same tolerance HttpHeaderParser already has.
        var a = HttpHeaderParser.ParseHeaderLines(headersA)
            .ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
        var b = HttpHeaderParser.ParseHeaderLines(headersB)
            .ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

        var names = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase);
        var diff = new List<HeaderDiffEntry>();
        foreach (var name in names)
        {
            var hasA = a.TryGetValue(name, out var valueA);
            var hasB = b.TryGetValue(name, out var valueB);
            if (hasA && hasB && valueA == valueB) continue; // unchanged — omit to keep the payload small
            diff.Add(new HeaderDiffEntry(name, hasA ? valueA : null, hasB ? valueB : null));
        }
        return diff;
    }

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

    public record CaptureDiffSummary(Guid Id, string Method, string Url, int StatusCode, DateTimeOffset Timestamp);

    /// <summary>A header present with a different value on each side, or present on only one side (the other value is null).</summary>
    public record HeaderDiffEntry(string Name, string? ValueA, string? ValueB);

    public record CaptureDiffResult(
        CaptureDiffSummary A,
        CaptureDiffSummary B,
        bool MethodChanged,
        bool UrlChanged,
        bool StatusCodeChanged,
        List<HeaderDiffEntry> RequestHeaderDiff,
        List<HeaderDiffEntry> ResponseHeaderDiff,
        bool RequestBodyEqual,
        bool ResponseBodyEqual);

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
