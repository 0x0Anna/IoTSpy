using System.Text;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Utilities;

namespace IoTSpy.Protocols.Rtsp;

/// <summary>
/// Decodes raw bytes into RTSP messages per RFC 2326. RTSP is a text-based control
/// protocol, syntactically very similar to HTTP/1.1, typically carried over TCP port 554.
/// </summary>
public sealed class RtspDecoder : IProtocolDecoder<RtspMessage>
{
    private static readonly Dictionary<string, RtspMethod> Methods = new(StringComparer.Ordinal)
    {
        ["DESCRIBE"] = RtspMethod.Describe,
        ["SETUP"] = RtspMethod.Setup,
        ["PLAY"] = RtspMethod.Play,
        ["PAUSE"] = RtspMethod.Pause,
        ["TEARDOWN"] = RtspMethod.Teardown,
        ["OPTIONS"] = RtspMethod.Options,
        ["ANNOUNCE"] = RtspMethod.Announce,
        ["RECORD"] = RtspMethod.Record,
        ["GET_PARAMETER"] = RtspMethod.GetParameter,
        ["SET_PARAMETER"] = RtspMethod.SetParameter,
        ["REDIRECT"] = RtspMethod.Redirect
    };

    private const string ResponsePrefix = "RTSP/1.0 ";

    /// <summary>
    /// Sniffs for RTSP: a request line starting with a known method token followed by a
    /// space and a URL, or a response starting with "RTSP/1.0 ". A leading '$' marks RTSP's
    /// interleaved binary data framing (RFC 2326 §10.12) — that's RTP, not RTSP text, so it
    /// is deliberately excluded here (see <see cref="RtpDecoder"/>).
    /// </summary>
    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length == 0) return false;

        // '$' introduces interleaved binary (RTP) data, not an RTSP text message.
        if (header[0] == (byte)'$') return false;

        var length = Math.Min(header.Length, 32);
        string text;
        try
        {
            text = Encoding.ASCII.GetString(header[..length]);
        }
        catch
        {
            return false;
        }

        if (text.StartsWith(ResponsePrefix, StringComparison.Ordinal)) return true;

        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex <= 0) return false;

        var token = text[..spaceIndex];
        return Methods.ContainsKey(token);
    }

    public Task<IReadOnlyList<RtspMessage>> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var messages = new List<RtspMessage>();

        if (TryDecode(data.Span, out var message))
            messages.Add(message);

        return Task.FromResult<IReadOnlyList<RtspMessage>>(messages);
    }

    private static bool TryDecode(ReadOnlySpan<byte> span, out RtspMessage message)
    {
        message = default!;

        if (span.Length == 0 || span[0] == (byte)'$') return false;

        string text;
        try
        {
            text = Encoding.ASCII.GetString(span);
        }
        catch
        {
            return false;
        }

        // Header/body boundary: blank line (CRLF CRLF, tolerating bare LF).
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (headerEnd < 0)
        {
            headerEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }

        // No blank line found: treat the whole buffer as headers-only (no body), as long
        // as it at least contains a terminated start line.
        var headerText = headerEnd >= 0 ? text[..headerEnd] : text.TrimEnd('\0');

        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0) return false;

        var startLine = lines[0];
        bool isResponse;
        RtspMethod method;
        string? uri = null;
        string version;
        int? statusCode = null;
        string? reasonPhrase = null;

        if (startLine.StartsWith(ResponsePrefix, StringComparison.Ordinal))
        {
            isResponse = true;
            method = RtspMethod.Response;
            var parts = startLine.Split(' ', 3);
            if (parts.Length < 2) return false;
            version = parts[0];
            if (!int.TryParse(parts[1], out var code)) return false;
            statusCode = code;
            reasonPhrase = parts.Length > 2 ? parts[2] : string.Empty;
        }
        else
        {
            var parts = startLine.Split(' ', 3);
            if (parts.Length < 3) return false;
            if (!Methods.TryGetValue(parts[0], out method)) return false;
            isResponse = false;
            uri = parts[1];
            version = parts[2];
            if (!version.StartsWith("RTSP/", StringComparison.Ordinal)) return false;
        }

        var headerBlockStart = startLine.Length;
        var headerRaw = headerText.Length > headerBlockStart ? headerText[headerBlockStart..] : string.Empty;
        var headers = HttpHeaderParser.ParseHeaderLines(headerRaw);

        byte[]? body = null;
        if (headerEnd >= 0)
        {
            var bodyStartChar = headerEnd + separatorLength;
            if (bodyStartChar < text.Length)
            {
                // Map the char offset back to a byte offset (ASCII: 1:1).
                var bodyStartByte = bodyStartChar;
                if (bodyStartByte < span.Length)
                    body = span[bodyStartByte..].ToArray();
            }
        }

        message = new RtspMessage
        {
            Method = method,
            IsResponse = isResponse,
            Uri = uri,
            Version = version,
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase,
            Headers = headers,
            Body = body,
            TotalLength = span.Length,
            RawBytes = span.ToArray()
        };

        return true;
    }
}
