using IoTSpy.Protocols.Rtsp;
using Xunit;

namespace IoTSpy.Protocols.Tests;

public class RtpDecoderTests
{
    private readonly RtpDecoder _decoder = new();

    // ── CanDecode ────────────────────────────────────────────────────────────

    [Fact]
    public void CanDecode_TooShort_ReturnsFalse()
    {
        Assert.False(_decoder.CanDecode([0x80, 0x60, 0x00]));
    }

    [Fact]
    public void CanDecode_ValidVersion2Header_ReturnsTrue()
    {
        byte[] header = [0x80, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01];
        Assert.True(_decoder.CanDecode(header));
    }

    [Fact]
    public void CanDecode_WrongVersion_ReturnsFalse()
    {
        // Version bits = 0 (byte0 = 0x00), not the required RTP version 2.
        byte[] header = [0x00, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01];
        Assert.False(_decoder.CanDecode(header));
    }

    [Fact]
    public void CanDecode_InterleavedFramingWithRtpInside_ReturnsTrue()
    {
        // '$' + channel 0 + length 0x000C, then a version-2 RTP header.
        byte[] frame =
        [
            (byte)'$', 0x00, 0x00, 0x0C,
            0x80, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01
        ];
        Assert.True(_decoder.CanDecode(frame));
    }

    // ── DecodeAsync: valid packet ─────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_ValidPacket_DecodesAllFields()
    {
        // Version=2, Padding=0, Extension=0, CC=0 → byte0 = 1000 0000 = 0x80
        // Marker=0, PayloadType=96 (0x60) → byte1 = 0110 0000 = 0x60
        // SequenceNumber = 0x0001
        // Timestamp = 0x00000064 (100)
        // SSRC = 0x12345678
        // Payload = "AV" (0x41, 0x56)
        byte[] data =
        [
            0x80, 0x60, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x64,
            0x12, 0x34, 0x56, 0x78,
            0x41, 0x56
        ];

        var packets = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(packets);
        var packet = packets[0];
        Assert.Equal(2, packet.Version);
        Assert.False(packet.Padding);
        Assert.False(packet.Extension);
        Assert.Equal(0, packet.CsrcCount);
        Assert.False(packet.Marker);
        Assert.Equal(96, packet.PayloadType);
        Assert.Equal(1, packet.SequenceNumber);
        Assert.Equal(100u, packet.Timestamp);
        Assert.Equal(0x12345678u, packet.Ssrc);
        Assert.Equal(2, packet.PayloadLength);
        Assert.Equal([0x41, 0x56], packet.Payload);
    }

    [Fact]
    public async Task DecodeAsync_MarkerBitSet_DecodesMarkerTrue()
    {
        // byte1 = 1110 0000 → marker=1, payloadType=96 (0x60 | 0x80 = 0xE0)
        byte[] data =
        [
            0x80, 0xE0, 0x00, 0x02,
            0x00, 0x00, 0x00, 0xC8,
            0x00, 0x00, 0x00, 0x2A
        ];

        var packets = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(packets);
        Assert.True(packets[0].Marker);
        Assert.Equal(96, packets[0].PayloadType);
    }

    [Fact]
    public async Task DecodeAsync_InterleavedFraming_StripsFrameAndDecodesRtp()
    {
        byte[] rtp =
        [
            0x80, 0x60, 0x00, 0x05,
            0x00, 0x00, 0x00, 0xC8,
            0x00, 0x00, 0x00, 0x2A
        ];
        byte[] frame = [(byte)'$', 0x00, 0x00, (byte)rtp.Length, .. rtp];

        var packets = await _decoder.DecodeAsync(frame, TestContext.Current.CancellationToken);

        Assert.Single(packets);
        Assert.Equal(5, packets[0].SequenceNumber);
        Assert.Equal(0x2Au, packets[0].Ssrc);
    }

    // ── DecodeAsync: edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_TooShortBuffer_ReturnsEmpty()
    {
        byte[] data = [0x80, 0x60, 0x00, 0x01];

        var packets = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(packets);
    }

    [Fact]
    public async Task DecodeAsync_EmptyBuffer_ReturnsEmpty()
    {
        var packets = await _decoder.DecodeAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken);

        Assert.Empty(packets);
    }

    [Fact]
    public async Task DecodeAsync_WrongVersion_ReturnsEmpty()
    {
        // Version bits = 1, not 2 — CanDecode would reject this too, but DecodeAsync must
        // independently guard against a caller skipping the CanDecode sniff.
        byte[] data =
        [
            0x40, 0x60, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x64,
            0x00, 0x00, 0x00, 0x01
        ];

        var packets = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(packets);
    }
}
