using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Manipulation.ApiSpec;

/// <summary>
/// Generates OpenAPI 3.0 specifications from captured HTTP traffic.
/// Analyzes request/response patterns to infer schemas, path parameters, and content types.
/// </summary>
public sealed class ApiSpecGenerator(
    IServiceScopeFactory scopeFactory,
    ILogger<ApiSpecGenerator> logger)
{
    private static readonly Regex GuidSegment = new(@"^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    // Signed integers too: some APIs (e.g. SleepIQ) use negative 64-bit ids as path segments.
    private static readonly Regex NumericSegment = new(@"^-?\d+$", RegexOptions.Compiled);
    private static readonly Regex HexSegment = new(@"^[0-9a-fA-F]{16,}$", RegexOptions.Compiled);

    public async Task<string> GenerateAsync(ApiSpecGenerationRequest request, CancellationToken ct = default)
    {
        var captures = await FetchCapturesAsync(request, ct);
        if (captures.Count == 0)
        {
            logger.LogWarning("No captures found for host {Host}", request.Host);
            return BuildEmptySpec(request.Host);
        }

        logger.LogInformation("Generating OpenAPI spec from {Count} captures for {Host}", captures.Count, request.Host);

        var endpoints = GroupByEndpoint(captures, request.PathPattern);
        var spec = BuildOpenApiDocument(request.Host, endpoints);
        return spec.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<List<CapturedRequest>> FetchCapturesAsync(ApiSpecGenerationRequest request, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var captureRepo = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();

        var filter = new CaptureFilter(
            HostContains: request.Host,
            Method: request.Method,
            From: request.From,
            To: request.To);

        return await captureRepo.GetPagedAsync(filter, page: 1, pageSize: 500, ct);
    }

    private Dictionary<string, List<CapturedRequest>> GroupByEndpoint(
        List<CapturedRequest> captures, string? pathPattern)
    {
        var groups = new Dictionary<string, List<CapturedRequest>>();

        foreach (var capture in captures)
        {
            if (pathPattern is not null &&
                !Regex.IsMatch(capture.Path, pathPattern, RegexOptions.IgnoreCase))
                continue;

            var normalizedPath = NormalizePath(capture.Path);
            var key = $"{capture.Method} {normalizedPath}";

            if (!groups.ContainsKey(key))
                groups[key] = [];
            groups[key].Add(capture);
        }

        return groups;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (GuidSegment.IsMatch(segments[i]))
                segments[i] = "{id}";
            else if (NumericSegment.IsMatch(segments[i]) && i > 0)
                segments[i] = "{id}";
            else if (HexSegment.IsMatch(segments[i]))
                segments[i] = "{id}";
        }

        return "/" + string.Join("/", segments);
    }

    private static JsonObject BuildOpenApiDocument(string host, Dictionary<string, List<CapturedRequest>> endpoints)
    {
        var paths = new JsonObject();

        foreach (var (key, captures) in endpoints)
        {
            var parts = key.Split(' ', 2);
            var method = parts[0].ToLowerInvariant();
            var path = parts[1];

            if (!paths.ContainsKey(path))
                paths[path] = new JsonObject();

            var pathItem = paths[path]!.AsObject();
            pathItem[method] = BuildOperation(method, path, captures);
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = $"API Spec for {host}",
                ["description"] = $"Auto-generated from {endpoints.Values.Sum(g => g.Count)} captured requests",
                ["version"] = "1.0.0"
            },
            ["servers"] = new JsonArray
            {
                new JsonObject { ["url"] = $"https://{host}" }
            },
            ["paths"] = paths
        };
    }

    private static JsonObject BuildOperation(string method, string path, List<CapturedRequest> captures)
    {
        var operation = new JsonObject
        {
            ["summary"] = $"{method.ToUpperInvariant()} {path}",
            ["operationId"] = GenerateOperationId(method, path)
        };

        // Path parameters
        var pathParams = ExtractPathParams(path);
        if (pathParams.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var param in pathParams)
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = param,
                    ["in"] = "path",
                    ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" }
                });
            }
            operation["parameters"] = parameters;
        }

        // Query parameters from observed traffic
        var queryParams = ExtractQueryParams(captures);
        if (queryParams.Count > 0)
        {
            var parameters = operation.ContainsKey("parameters")
                ? operation["parameters"]!.AsArray()
                : new JsonArray();

            foreach (var (name, values) in queryParams)
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = name,
                    ["in"] = "query",
                    ["required"] = false,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                    ["example"] = values.FirstOrDefault() ?? ""
                });
            }

            if (!operation.ContainsKey("parameters"))
                operation["parameters"] = parameters;
        }

        // Request body (for POST/PUT/PATCH)
        if (method is "post" or "put" or "patch")
        {
            var requestBody = BuildRequestBody(captures);
            if (requestBody is not null)
                operation["requestBody"] = requestBody;
        }

        // Responses grouped by status code
        operation["responses"] = BuildResponses(captures);

        return operation;
    }

    private static JsonObject BuildResponses(List<CapturedRequest> captures)
    {
        var responses = new JsonObject();
        var byStatus = captures.GroupBy(c => c.StatusCode).OrderBy(g => g.Key);

        foreach (var group in byStatus)
        {
            var statusCode = group.Key.ToString();
            var sample = group.First();
            var contentType = ExtractHeaderValue(sample.ResponseHeaders, "Content-Type") ?? "application/json";
            var mediaType = contentType.Split(';')[0].Trim();

            var responseObj = new JsonObject
            {
                ["description"] = $"Status {statusCode} response (observed {group.Count()} times)"
            };

            // Try to infer schema from response body
            var schema = TryInferSchema(sample.ResponseBody, mediaType);
            if (schema is not null)
            {
                responseObj["content"] = new JsonObject
                {
                    [mediaType] = new JsonObject
                    {
                        ["schema"] = schema
                    }
                };

                // Add example from first sample (truncated)
                if (!string.IsNullOrWhiteSpace(sample.ResponseBody) && sample.ResponseBody.Length < 2000)
                {
                    try
                    {
                        var exampleNode = JsonNode.Parse(sample.ResponseBody);
                        if (exampleNode is not null)
                            responseObj["content"]![mediaType]!.AsObject()["example"] = exampleNode;
                    }
                    catch { /* not valid JSON, skip example */ }
                }
            }

            responses[statusCode] = responseObj;
        }

        if (responses.Count == 0)
            responses["200"] = new JsonObject { ["description"] = "Success" };

        return responses;
    }

    private static JsonObject? BuildRequestBody(List<CapturedRequest> captures)
    {
        var withBody = captures.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.RequestBody));
        if (withBody is null) return null;

        var contentType = ExtractHeaderValue(withBody.RequestHeaders, "Content-Type") ?? "application/json";
        var mediaType = contentType.Split(';')[0].Trim();
        var schema = TryInferSchema(withBody.RequestBody, mediaType);

        if (schema is null) return null;

        var body = new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject
            {
                [mediaType] = new JsonObject { ["schema"] = schema }
            }
        };

        return body;
    }

    internal static JsonObject? TryInferSchema(string? body, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        if (mediaType.Contains("json"))
        {
            try
            {
                var node = JsonNode.Parse(body);
                return InferJsonSchema(node);
            }
            catch
            {
                return new JsonObject { ["type"] = "string" };
            }
        }

        if (mediaType.StartsWith("image/"))
            return new JsonObject { ["type"] = "string", ["format"] = "binary" };

        if (mediaType.StartsWith("video/"))
            return new JsonObject { ["type"] = "string", ["format"] = "binary" };

        if (mediaType.StartsWith("audio/"))
            return new JsonObject { ["type"] = "string", ["format"] = "binary" };

        if (mediaType.Contains("xml"))
            return new JsonObject { ["type"] = "string", ["format"] = "xml" };

        return new JsonObject { ["type"] = "string" };
    }

    internal static JsonObject InferJsonSchema(JsonNode? node)
    {
        if (node is null)
            return new JsonObject { ["nullable"] = true };

        if (node is JsonObject obj)
        {
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var (key, value) in obj)
            {
                properties[key] = InferJsonSchema(value);
                required.Add(key);
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };

            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }

        if (node is JsonArray arr)
        {
            var schema = new JsonObject { ["type"] = "array" };
            if (arr.Count > 0)
                schema["items"] = InferJsonSchema(arr[0]);
            else
                schema["items"] = new JsonObject();
            return schema;
        }

        if (node is JsonValue val)
        {
            var element = val.GetValue<JsonElement>();
            return element.ValueKind switch
            {
                JsonValueKind.String => InferStringSchema(element.GetString()),
                JsonValueKind.Number => element.TryGetInt64(out _)
                    ? new JsonObject { ["type"] = "integer" }
                    : new JsonObject { ["type"] = "number" },
                JsonValueKind.True or JsonValueKind.False => new JsonObject { ["type"] = "boolean" },
                JsonValueKind.Null => new JsonObject { ["nullable"] = true },
                _ => new JsonObject()
            };
        }

        return new JsonObject();
    }

    private static JsonObject InferStringSchema(string? value)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (value is null) return schema;

        if (GuidSegment.IsMatch(value))
            schema["format"] = "uuid";
        else if (DateTimeOffset.TryParse(value, out _))
            schema["format"] = "date-time";
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            schema["format"] = "uri";
        else if (value.Contains('@') && value.Contains('.'))
            schema["format"] = "email";

        return schema;
    }

    private static List<string> ExtractPathParams(string path)
    {
        var matches = Regex.Matches(path, @"\{(\w+)\}");
        return matches.Select(m => m.Groups[1].Value).ToList();
    }

    private static Dictionary<string, List<string>> ExtractQueryParams(List<CapturedRequest> captures)
    {
        var result = new Dictionary<string, List<string>>();

        foreach (var capture in captures)
        {
            if (string.IsNullOrWhiteSpace(capture.Query)) continue;

            var query = capture.Query.TrimStart('?');
            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=', 2);
                var name = Uri.UnescapeDataString(kv[0]);
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";

                if (!result.ContainsKey(name))
                    result[name] = [];
                if (!result[name].Contains(value))
                    result[name].Add(value);
            }
        }

        return result;
    }

    private static string GenerateOperationId(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.StartsWith('{'))
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..]);
        return method + string.Join("", segments);
    }

    internal static string? ExtractHeaderValue(string headersJson, string headerName)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return null;
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                {
                    if (key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
        }
        catch { /* headers may not be valid JSON */ }
        return null;
    }

    private static string BuildEmptySpec(string host)
    {
        var spec = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = $"API Spec for {host}",
                ["description"] = "No traffic captured yet. Capture traffic through the proxy to generate endpoints.",
                ["version"] = "1.0.0"
            },
            ["servers"] = new JsonArray
            {
                new JsonObject { ["url"] = $"https://{host}" }
            },
            ["paths"] = new JsonObject()
        };
        return spec.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
