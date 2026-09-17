using System.Text;
using IoTSpy.Protocols.Rtsp;
using Xunit;

namespace IoTSpy.Protocols.Tests;

public class RtspDecoderTests
{
    private readonly RtspDecoder _decoder = new();

    // ── CanDecode ────────────────────────────────────────────────────────────

    [Fact]
    public void CanDecode_EmptyBuffer_ReturnsFalse()
    {
        Assert.False(_decoder.CanDecode([]));
    }

    [Fact]
    public void CanDecode_DescribeRequest_ReturnsTrue()
    {
        var bytes = Encoding.ASCII.GetBytes("DESCRIBE rtsp://example.com/stream RTSP/1.0\r\n");
        Assert.True(_decoder.CanDecode(bytes));
    }

    [Fact]
    public void CanDecode_ResponseStatusLine_ReturnsTrue()
    {
        var bytes = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\n");
        Assert.True(_decoder.CanDecode(bytes));
    }

    [Fact]
    public void CanDecode_UnknownMethod_ReturnsFalse()
    {
        var bytes = Encoding.ASCII.GetBytes("FOOBAR rtsp://example.com/stream RTSP/1.0\r\n");
        Assert.False(_decoder.CanDecode(bytes));
    }

    [Fact]
    public void CanDecode_InterleavedBinaryMarker_ReturnsFalse()
    {
        // '$' introduces RTP/RTCP interleaved data, not RTSP text — routed to RtpDecoder instead.
        byte[] bytes = [(byte)'$', 0x00, 0x00, 0x0C];
        Assert.False(_decoder.CanDecode(bytes));
    }

    // ── DecodeAsync: DESCRIBE request ────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_DescribeRequest_DecodesCorrectly()
    {
        var text = "DESCRIBE rtsp://192.168.1.10/stream1 RTSP/1.0\r\n" +
                    "CSeq: 1\r\n" +
                    "Accept: application/sdp\r\n" +
                    "\r\n";
        var data = Encoding.ASCII.GetBytes(text);

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.False(msg.IsResponse);
        Assert.Equal(RtspMethod.Describe, msg.Method);
        Assert.Equal("rtsp://192.168.1.10/stream1", msg.Uri);
        Assert.Equal(1, msg.CSeq);
        Assert.False(msg.HasAuthorizationHeader);
    }

    // ── DecodeAsync: SETUP request with Authorization ────────────────────────

    [Fact]
    public async Task DecodeAsync_SetupRequestWithAuthorization_HasAuthorizationHeaderTrue()
    {
        var text = "SETUP rtsp://192.168.1.10/stream1/track1 RTSP/1.0\r\n" +
                    "CSeq: 2\r\n" +
                    "Authorization: Basic dXNlcjpwYXNz\r\n" +
                    "Transport: RTP/AVP;unicast;client_port=8000-8001\r\n" +
                    "\r\n";
        var data = Encoding.ASCII.GetBytes(text);

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(RtspMethod.Setup, msg.Method);
        Assert.Equal(2, msg.CSeq);
        Assert.True(msg.HasAuthorizationHeader);
    }

    // ── DecodeAsync: 200 OK response with SDP body ───────────────────────────

    [Fact]
    public async Task DecodeAsync_DescribeResponseWithSdpBody_ParsesSessionAndMedia()
    {
        var sdp = "v=0\r\n" +
                  "o=- 12345 1 IN IP4 192.168.1.10\r\n" +
                  "s=IoTSpy Test Stream\r\n" +
                  "c=IN IP4 224.2.17.12\r\n" +
                  "t=0 0\r\n" +
                  "m=video 0 RTP/AVP 96\r\n" +
                  "a=rtpmap:96 H264/90000\r\n" +
                  "m=audio 0 RTP/AVP 97\r\n";
        var sdpBytes = Encoding.ASCII.GetBytes(sdp);

        var headerText = "RTSP/1.0 200 OK\r\n" +
                          "CSeq: 1\r\n" +
                          "Content-Type: application/sdp\r\n" +
                          $"Content-Length: {sdpBytes.Length}\r\n" +
                          "\r\n";
        var data = Encoding.ASCII.GetBytes(headerText).Concat(sdpBytes).ToArray();

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.True(msg.IsResponse);
        Assert.Equal(200, msg.StatusCode);
        Assert.Equal("OK", msg.ReasonPhrase);
        Assert.NotNull(msg.Sdp);
        Assert.Equal("IoTSpy Test Stream", msg.Sdp!.SessionName);
        Assert.Equal("IN IP4 224.2.17.12", msg.Sdp.ConnectionAddress);
        Assert.Equal(2, msg.Sdp.MediaDescriptions.Count);
        Assert.Equal("video", msg.Sdp.MediaDescriptions[0].MediaType);
        Assert.Equal("RTP/AVP", msg.Sdp.MediaDescriptions[0].Protocol);
        Assert.Equal("96", msg.Sdp.MediaDescriptions[0].Formats[0]);
        Assert.Equal("audio", msg.Sdp.MediaDescriptions[1].MediaType);
    }

    // ── DecodeAsync: unauthenticated stream flag ─────────────────────────────

    [Fact]
    public async Task DecodeAsync_PlayResponse200WithNoAuthHeader_FlagsUnauthenticatedSignal()
    {
        var text = "RTSP/1.0 200 OK\r\n" +
                    "CSeq: 3\r\n" +
                    "Session: 12345678\r\n" +
                    "\r\n";
        var data = Encoding.ASCII.GetBytes(text);

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.True(msg.IsUnauthenticatedStreamSignal);
    }

    [Fact]
    public async Task DecodeAsync_UnauthorizedResponseWithChallenge_HasAuthChallengeTrue()
    {
        var text = "RTSP/1.0 401 Unauthorized\r\n" +
                    "CSeq: 2\r\n" +
                    "WWW-Authenticate: Digest realm=\"IoTSpy\", nonce=\"abc123\"\r\n" +
                    "\r\n";
        var data = Encoding.ASCII.GetBytes(text);

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(401, msg.StatusCode);
        Assert.True(msg.HasAuthChallenge);
        Assert.False(msg.IsUnauthenticatedStreamSignal);
    }

    // ── DecodeAsync: edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_EmptyBuffer_ReturnsEmpty()
    {
        var messages = await _decoder.DecodeAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task DecodeAsync_TruncatedRequestLine_ReturnsEmpty()
    {
        // Missing URI and version entirely.
        var data = Encoding.ASCII.GetBytes("DESCRIBE\r\n");

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task DecodeAsync_UnknownMethodToken_ReturnsEmpty()
    {
        var data = Encoding.ASCII.GetBytes("FOOBAR rtsp://example.com/stream RTSP/1.0\r\n\r\n");

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }
}
