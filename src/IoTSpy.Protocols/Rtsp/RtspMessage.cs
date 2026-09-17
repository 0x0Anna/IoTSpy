namespace IoTSpy.Protocols.Rtsp;

/// <summary>
/// RTSP request methods per RFC 2326, plus <see cref="Response"/> for status-line messages.
/// </summary>
public enum RtspMethod
{
    Describe,
    Setup,
    Play,
    Pause,
    Teardown,
    Options,
    Announce,
    Record,
    GetParameter,
    SetParameter,
    Redirect,
    Response,
    Unknown
}

/// <summary>
/// Represents a decoded RTSP request or response message (RFC 2326).
/// RTSP is syntactically similar to HTTP/1.1: a request/status line, colon-delimited
/// headers terminated by CRLF, a blank line, then an optional body.
/// </summary>
public sealed class RtspMessage
{
    /// <summary>Decoded method for requests, or <see cref="RtspMethod.Response"/> for status lines.</summary>
    public RtspMethod Method { get; init; }

    /// <summary>True when this message is a response (status line), false for requests.</summary>
    public bool IsResponse { get; init; }

    /// <summary>Request URI (requests only).</summary>
    public string? Uri { get; init; }

    /// <summary>RTSP protocol version string, e.g. "RTSP/1.0".</summary>
    public string Version { get; init; } = "RTSP/1.0";

    /// <summary>Numeric status code (responses only).</summary>
    public int? StatusCode { get; init; }

    /// <summary>Reason phrase (responses only), e.g. "OK".</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>Decoded headers as name/value pairs, in wire order.</summary>
    public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];

    /// <summary>Raw message body bytes (after the blank line), if any.</summary>
    public byte[]? Body { get; init; }

    /// <summary>Body decoded as UTF-8 (best-effort). Null when <see cref="Body"/> is null.</summary>
    public string? BodyString => Body is null ? null : System.Text.Encoding.UTF8.GetString(Body);

    /// <summary>Raw bytes of the entire decoded message.</summary>
    public byte[]? RawBytes { get; init; }

    /// <summary>Total decoded length in bytes.</summary>
    public int TotalLength { get; init; }

    // ── Convenience accessors derived from headers ───────────────────────

    /// <summary>CSeq header value, used to correlate RTSP requests/responses.</summary>
    public int? CSeq =>
        FindHeader("CSeq") is { } value && int.TryParse(value, out var seq) ? seq : null;

    /// <summary>Session header value (assigned by the server on SETUP).</summary>
    public string? Session => FindHeader("Session");

    /// <summary>Content-Type header value.</summary>
    public string? ContentType => FindHeader("Content-Type");

    /// <summary>True when the request carries an Authorization header (Basic/Digest).</summary>
    public bool HasAuthorizationHeader => FindHeader("Authorization") is not null;

    /// <summary>True when a 401 response carries a WWW-Authenticate challenge.</summary>
    public bool HasAuthChallenge => FindHeader("WWW-Authenticate") is not null;

    /// <summary>
    /// True when this message is a 200 OK response to a SETUP/PLAY request with no
    /// corresponding Authorization header on *this* message — a stateless signal only;
    /// correlating it to the originating request is left to the caller.
    /// </summary>
    public bool IsUnauthenticatedStreamSignal =>
        IsResponse && StatusCode == 200 && !HasAuthorizationHeader;

    /// <summary>
    /// Parsed SDP body (RFC 4566), populated when this is a DESCRIBE response whose
    /// Content-Type is "application/sdp".
    /// </summary>
    public SdpInfo? Sdp =>
        IsResponse
        && Body is not null
        && ContentType is { } ct
        && ct.Contains("application/sdp", StringComparison.OrdinalIgnoreCase)
            ? SdpInfo.Parse(BodyString!)
            : null;

    private string? FindHeader(string name)
    {
        foreach (var (headerName, value) in Headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    public override string ToString() =>
        IsResponse
            ? $"RTSP {Version} {StatusCode} {ReasonPhrase}"
            : $"RTSP {Method} {Uri}";
}

/// <summary>
/// Lightweight SDP (RFC 4566) session description — only the headline fields needed to
/// surface stream metadata: session name, connection address, and media descriptions.
/// </summary>
public sealed record SdpInfo
{
    /// <summary>Session name (s= line).</summary>
    public string? SessionName { get; init; }

    /// <summary>Connection address (c= line), e.g. "IN IP4 224.2.17.12".</summary>
    public string? ConnectionAddress { get; init; }

    /// <summary>Media descriptions (m= lines).</summary>
    public IReadOnlyList<SdpMediaDescription> MediaDescriptions { get; init; } = [];

    /// <summary>
    /// Parses an SDP body into its headline fields. Best-effort: unrecognized/malformed
    /// lines are skipped rather than throwing.
    /// </summary>
    public static SdpInfo Parse(string body)
    {
        string? sessionName = null;
        string? connectionAddress = null;
        var mediaDescriptions = new List<SdpMediaDescription>();

        foreach (var rawLine in body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[1] != '=') continue;

            var value = line[2..];
            switch (line[0])
            {
                case 's':
                    sessionName ??= value;
                    break;
                case 'c':
                    connectionAddress ??= value;
                    break;
                case 'm':
                    if (SdpMediaDescription.TryParse(value, out var media))
                        mediaDescriptions.Add(media);
                    break;
            }
        }

        return new SdpInfo
        {
            SessionName = sessionName,
            ConnectionAddress = connectionAddress,
            MediaDescriptions = mediaDescriptions
        };
    }
}

/// <summary>
/// A single SDP media description (m= line), e.g. "video 0 RTP/AVP 96".
/// </summary>
public sealed record SdpMediaDescription(string MediaType, int Port, string Protocol, IReadOnlyList<string> Formats)
{
    internal static bool TryParse(string value, out SdpMediaDescription media)
    {
        media = default!;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var mediaType = parts[0];
        // Port may be "port" or "port/count"; take the numeric prefix.
        var portToken = parts[1].Split('/')[0];
        if (!int.TryParse(portToken, out var port)) return false;

        var protocol = parts[2];
        var formats = parts.Length > 3 ? parts[3..] : [];

        media = new SdpMediaDescription(mediaType, port, protocol, formats);
        return true;
    }
}
