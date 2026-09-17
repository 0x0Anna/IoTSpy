using System.Diagnostics;
using System.Text;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Manipulation;

/// <summary>
/// Mutation-based fuzzer that sends variants of a captured request and detects anomalous responses.
/// </summary>
public class FuzzerService(IHttpClientFactory httpClientFactory, ILogger<FuzzerService> logger)
{
    public async Task<FuzzerResult> ExecuteMutationAsync(
        CapturedRequest baseCapture, int mutationIndex, FuzzerStrategy strategy,
        bool bypassTlsValidation = false, CancellationToken ct = default)
    {
        var mutatedBody = Mutate(baseCapture.RequestBody, mutationIndex, strategy);
        var result = new FuzzerResult
        {
            MutationIndex = mutationIndex,
            MutationDescription = $"{strategy} mutation #{mutationIndex}",
            MutatedBody = mutatedBody
        };

        var sw = Stopwatch.StartNew();

        try
        {
            var scheme = baseCapture.Scheme.ToLowerInvariant();
            var uriBuilder = new UriBuilder(scheme, baseCapture.Host, baseCapture.Port, baseCapture.Path)
            {
                Query = baseCapture.Query
            };

            using var request = new HttpRequestMessage(new HttpMethod(baseCapture.Method), uriBuilder.Uri);

            // Copy original headers
            if (!string.IsNullOrEmpty(baseCapture.RequestHeaders))
            {
                foreach (var line in baseCapture.RequestHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    var name = line[..colonIdx].Trim();
                    var value = line[(colonIdx + 1)..].Trim();

                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;

                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (!string.IsNullOrEmpty(mutatedBody))
            {
                var contentType = ExtractHeaderValue(baseCapture.RequestHeaders, "Content-Type") ?? "application/octet-stream";
                request.Content = new StringContent(mutatedBody, Encoding.UTF8, contentType);
            }

            var clientName = bypassTlsValidation ? "IoTSpyFuzzerBypassTls" : "IoTSpyFuzzer";
            var client = httpClientFactory.CreateClient(clientName);
            using var response = await client.SendAsync(request, ct);

            result.ResponseStatusCode = (int)response.StatusCode;
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            // Truncate large responses
            result.ResponseBody = responseBody.Length > 4096 ? responseBody[..4096] : responseBody;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fuzzer mutation {Index} failed", mutationIndex);
            result.ResponseStatusCode = 0;
            result.ResponseBody = $"Error: {ex.Message}";
        }

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;

        // Detect anomalies: error status codes, timeouts, very different response times
        result.IsAnomaly = result.ResponseStatusCode is 0 or >= 500
                           || result.DurationMs > 10_000;
        if (result.IsAnomaly)
            result.AnomalyReason = result.ResponseStatusCode == 0
                ? "Connection error"
                : result.ResponseStatusCode >= 500
                    ? $"Server error: {result.ResponseStatusCode}"
                    : "Slow response (>10s)";

        ManipulationMetrics.RecordFuzzerRequest(
            result.ResponseStatusCode == 0 ? "error" : result.IsAnomaly ? "anomaly" : "ok");

        return result;
    }

    internal static string Mutate(string body, int index, FuzzerStrategy strategy)
    {
        if (string.IsNullOrEmpty(body)) return body;

        return strategy switch
        {
            FuzzerStrategy.Random => MutateRandom(body, index),
            FuzzerStrategy.Boundary => MutateBoundary(body, index),
            FuzzerStrategy.BitFlip => MutateBitFlip(body, index),
            _ => body
        };
    }

    private static string MutateRandom(string body, int index)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        // Use deterministic seed for reproducibility
        var rng = new Random(index);
        var numMutations = rng.Next(1, Math.Max(2, bytes.Length / 10));

        for (var i = 0; i < numMutations; i++)
        {
            var pos = rng.Next(bytes.Length);
            bytes[pos] = (byte)rng.Next(256);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string MutateBoundary(string body, int index)
    {
        // Replace numeric-looking values with boundary values
        string[] boundaryValues = [
            "0", "-1", "1", int.MaxValue.ToString(), int.MinValue.ToString(),
            long.MaxValue.ToString(), "", "null", "undefined", "NaN",
            "true", "false", new string('A', 1000),
            "<script>alert(1)</script>", "' OR 1=1 --", "{{template}}"
        ];

        var replacement = boundaryValues[index % boundaryValues.Length];

        // Try to replace a JSON value
        var jsonValuePattern = @"""[^""]*"":\s*""[^""]*""";
        var match = System.Text.RegularExpressions.Regex.Match(body, jsonValuePattern);
        if (match.Success)
        {
            var colonIdx = match.Value.IndexOf(':');
            var key = match.Value[..colonIdx];
            return body[..match.Index] + $"{key}: \"{replacement}\"" + body[(match.Index + match.Length)..];
        }

        // Fallback: append boundary value
        return body + replacement;
    }

    private static string MutateBitFlip(string body, int index)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length == 0) return body;

        var pos = index % bytes.Length;
        var bit = index / bytes.Length % 8;
        bytes[pos] ^= (byte)(1 << bit);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractHeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }
        return null;
    }
}
