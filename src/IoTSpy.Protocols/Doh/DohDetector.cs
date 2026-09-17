using IoTSpy.Core.Utilities;
using IoTSpy.Protocols.Dns;

namespace IoTSpy.Protocols.Doh;

/// <summary>
/// Detects DNS-over-HTTPS (RFC 8484) framing on already-decoded HTTP requests and, where
/// possible, decodes the embedded DNS message by delegating to <see cref="DnsDecoder"/>.
/// Mirrors the "detect on top of an already-decoded message" shape used by
/// <c>WebSocketDecoder.DetectSubProtocol</c> — this is not a standalone protocol decoder.
/// </summary>
public static class DohDetector
{
    private const string DnsMessageContentType = "application/dns-message";
    private const string DnsQueryPath = "/dns-query";

    private static readonly DnsDecoder Decoder = new();

    /// <summary>
    /// Attempts to detect RFC 8484 DoH framing in an HTTP request and, when possible,
    /// decode the wrapped DNS message. Never throws.
    /// </summary>
    /// <param name="method">HTTP method (e.g. "GET", "POST").</param>
    /// <param name="path">Request path, optionally including the query string (e.g. "/dns-query?dns=...").</param>
    /// <param name="headersRaw">Raw "Name: Value\r\n..." header text, as stored on <c>CapturedRequest</c>.</param>
    /// <param name="body">Raw request body, when present (used for POST framing).</param>
    public static DohDetectionResult TryDetect(string method, string? path, string? headersRaw, byte[]? body)
    {
        try
        {
            var (pathOnly, query) = SplitPathAndQuery(path);

            var contentType = HttpHeaderParser.FindHeaderValue(headersRaw, "Content-Type");
            var accept = HttpHeaderParser.FindHeaderValue(headersRaw, "Accept");

            var hasDnsMessageContentType =
                ContainsDnsMessageMediaType(contentType) || ContainsDnsMessageMediaType(accept);

            var isDnsQueryPath = string.Equals(pathOnly, DnsQueryPath, StringComparison.OrdinalIgnoreCase);
            var dnsParam = TryGetQueryParam(query, "dns");

            var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

            var isDoh =
                hasDnsMessageContentType ||
                (isDnsQueryPath && dnsParam is not null);

            if (!isDoh) return new DohDetectionResult(false, null);

            byte[]? wireBytes = null;

            if (isGet && dnsParam is not null)
            {
                wireBytes = TryBase64UrlDecode(dnsParam);
            }
            else if (isPost && body is { Length: > 0 })
            {
                wireBytes = body;
            }

            DnsMessage? decoded = null;
            if (wireBytes is { Length: > 0 })
            {
                try
                {
                    if (Decoder.TryDecode(wireBytes, out var msg))
                        decoded = msg;
                }
                catch
                {
                    // Malformed DNS wire bytes inside otherwise-valid DoH framing — detection
                    // still stands, decode result is simply unavailable.
                }
            }

            return new DohDetectionResult(true, decoded);
        }
        catch
        {
            // Detection must never throw — treat any parsing failure as "not DoH".
            return new DohDetectionResult(false, null);
        }
    }

    /// <summary>Overload accepting a raw string body (as HTTP bodies are often modeled in this codebase).</summary>
    public static DohDetectionResult TryDetect(string method, string? path, string? headersRaw, string? body)
    {
        byte[]? bodyBytes = null;
        if (!string.IsNullOrEmpty(body))
        {
            try { bodyBytes = System.Text.Encoding.Latin1.GetBytes(body); }
            catch { bodyBytes = null; }
        }
        return TryDetect(method, path, headersRaw, bodyBytes);
    }

    private static bool ContainsDnsMessageMediaType(string? headerValue) =>
        !string.IsNullOrEmpty(headerValue) &&
        headerValue.Contains(DnsMessageContentType, StringComparison.OrdinalIgnoreCase);

    private static (string Path, string Query) SplitPathAndQuery(string? path)
    {
        if (string.IsNullOrEmpty(path)) return (string.Empty, string.Empty);
        var qIdx = path.IndexOf('?');
        return qIdx < 0 ? (path, string.Empty) : (path[..qIdx], path[(qIdx + 1)..]);
    }

    private static string? TryGetQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = pair.IndexOf('=');
            var key = eqIdx < 0 ? pair : pair[..eqIdx];
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            return eqIdx < 0 ? string.Empty : pair[(eqIdx + 1)..];
        }
        return null;
    }

    private static byte[]? TryBase64UrlDecode(string value)
    {
        try
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Result of a <see cref="DohDetector.TryDetect(string,string?,string?,byte[]?)"/> call.
/// <see cref="IsDoh"/> is true whenever RFC 8484 framing was recognized, independent of
/// whether the embedded DNS message could actually be decoded (<see cref="Query"/>).
/// </summary>
public sealed record DohDetectionResult(bool IsDoh, DnsMessage? Query);
