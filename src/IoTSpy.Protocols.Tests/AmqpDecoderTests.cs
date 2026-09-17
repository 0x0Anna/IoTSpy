using IoTSpy.Protocols.Amqp;
using Xunit;

namespace IoTSpy.Protocols.Tests;

public class AmqpDecoderTests
{
    private readonly AmqpDecoder _decoder = new();

    // ── CanDecode ────────────────────────────────────────────────────────────

    [Fact]
    public void CanDecode_TooShort_ReturnsFalse()
    {
        Assert.False(_decoder.CanDecode([0x41, 0x4D, 0x51]));
    }

    [Fact]
    public void CanDecode_ProtocolHeaderMagic_ReturnsTrue()
    {
        // 'A' 'M' 'Q' 'P', protocol-id=0 (AMQP), major=1, minor=0, revision=0
        Assert.True(_decoder.CanDecode([0x41, 0x4D, 0x51, 0x50, 0x00, 0x01, 0x00, 0x00]));
    }

    [Fact]
    public void CanDecode_PlausibleFrameHeader_ReturnsTrue()
    {
        // size=38 (>=8), doff=2, type=0x00 (AMQP frame), channel=0
        Assert.True(_decoder.CanDecode([0x00, 0x00, 0x00, 0x26, 0x02, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void CanDecode_InvalidDataOffset_ReturnsFalse()
    {
        // doff=1 is invalid (frame header itself is 2 words = 8 bytes minimum)
        Assert.False(_decoder.CanDecode([0x00, 0x00, 0x00, 0x26, 0x01, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void CanDecode_InvalidTypeByte_ReturnsFalse()
    {
        // type=0xFF is neither AMQP(0x00) nor SASL(0x01), and not the protocol header magic
        Assert.False(_decoder.CanDecode([0x00, 0x00, 0x00, 0x26, 0x02, 0xFF, 0x00, 0x00]));
    }

    // ── DecodeAsync: protocol header ─────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_ProtocolHeader_DecodesCorrectly()
    {
        // "AMQP" + protocol-id=0 (AMQP, not SASL) + major=1, minor=0, revision=0
        byte[] data = [0x41, 0x4D, 0x51, 0x50, 0x00, 0x01, 0x00, 0x00];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(AmqpFrameType.ProtocolHeader, msg.FrameType);
        Assert.Equal((byte)0, msg.ProtocolId);
        Assert.Equal((byte)1, msg.MajorVersion);
        Assert.Equal((byte)0, msg.MinorVersion);
        Assert.Equal((byte)0, msg.Revision);
    }

    [Fact]
    public async Task DecodeAsync_SaslProtocolHeader_DecodesProtocolId()
    {
        // "AMQP" + protocol-id=3 (SASL) + major=1, minor=0, revision=0
        byte[] data = [0x41, 0x4D, 0x51, 0x50, 0x03, 0x01, 0x00, 0x00];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        Assert.Equal((byte)3, messages[0].ProtocolId);
    }

    // ── DecodeAsync: open frame ──────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_OpenFrame_DecodesContainerIdAndHostname()
    {
        // Frame body layout:
        //   0x00                    described-type constructor
        //   0x53, 0x10               descriptor: smallulong, code=0x10 (open performative)
        //   0xC0, 0x19, 0x02         list8: size=0x19(25 bytes follow), count=2 fields
        //     0xA1, 0x0B, "test-client"   field 0 (container-id): str8-utf8, len=11
        //     0xA1, 0x09, "iot.local"     field 1 (hostname): str8-utf8, len=9
        // Body total = 1 + 2 + 27 = 30 bytes. Frame total = 8 (header) + 30 (body) = 38 = 0x26.
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x26,  // frame size = 38
            0x02,                      // doff = 2 (no extended header)
            0x00,                      // type = 0x00 (AMQP frame)
            0x00, 0x00,                // channel = 0

            0x00,                      // described-type constructor
            0x53, 0x10,                // descriptor: smallulong = 0x10 (open)
            0xC0, 0x19, 0x02,          // list8, size=25, count=2

            0xA1, 0x0B,                // str8-utf8, len=11
            (byte)'t', (byte)'e', (byte)'s', (byte)'t', (byte)'-',
            (byte)'c', (byte)'l', (byte)'i', (byte)'e', (byte)'n', (byte)'t',

            0xA1, 0x09,                // str8-utf8, len=9
            (byte)'i', (byte)'o', (byte)'t', (byte)'.',
            (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l'
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(AmqpFrameType.Frame, msg.FrameType);
        Assert.Equal(38u, msg.FrameSize);
        Assert.Equal((byte)2, msg.DataOffset);
        Assert.Equal((ushort)0, msg.Channel);
        Assert.Equal(AmqpPerformativeType.Open, msg.Performative);
        Assert.Equal("test-client", msg.ContainerId);
        Assert.Equal("iot.local", msg.Hostname);
    }

    // ── DecodeAsync: transfer frame ──────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_TransferFrame_DecodesHandleAndDeliveryId()
    {
        // Frame body layout:
        //   0x00                described-type constructor
        //   0x53, 0x14           descriptor: smallulong, code=0x14 (transfer performative)
        //   0xC0, 0x05, 0x02     list8: size=5 bytes follow, count=2 fields
        //     0x52, 0x07             field 0 (handle): smalluint = 7
        //     0x52, 0x2A             field 1 (delivery-id): smalluint = 42
        // Body total = 1 + 2 + 7 = 10 bytes. Frame total = 8 + 10 = 18 = 0x12.
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x12,  // frame size = 18
            0x02,                      // doff = 2
            0x00,                      // type = 0x00
            0x00, 0x01,                // channel = 1

            0x00,                      // described-type constructor
            0x53, 0x14,                // descriptor: smallulong = 0x14 (transfer)
            0xC0, 0x05, 0x02,          // list8, size=5, count=2

            0x52, 0x07,                // smalluint = 7 (handle)
            0x52, 0x2A                 // smalluint = 42 (delivery-id)
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(AmqpPerformativeType.Transfer, msg.Performative);
        Assert.Equal((ushort)1, msg.Channel);
        Assert.Equal(7u, msg.Handle);
        Assert.Equal(42u, msg.DeliveryId);
    }

    // ── DecodeAsync: multiple frames in one buffer ───────────────────────────

    [Fact]
    public async Task DecodeAsync_ProtocolHeaderFollowedByEmptyFrame_DecodesBoth()
    {
        byte[] protocolHeader = [0x41, 0x4D, 0x51, 0x50, 0x00, 0x01, 0x00, 0x00];
        byte[] emptyFrame =
        [
            0x00, 0x00, 0x00, 0x08,  // frame size = 8 (header only, no body — e.g. a heartbeat)
            0x02,                      // doff = 2
            0x00,                      // type = 0x00
            0x00, 0x00                 // channel = 0
        ];

        var data = new List<byte>();
        data.AddRange(protocolHeader);
        data.AddRange(emptyFrame);

        var messages = await _decoder.DecodeAsync(data.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
        Assert.Equal(AmqpFrameType.ProtocolHeader, messages[0].FrameType);
        Assert.Equal(AmqpFrameType.Frame, messages[1].FrameType);
        Assert.Equal(AmqpPerformativeType.Unknown, messages[1].Performative);
    }

    // ── DecodeAsync: edge cases ──────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_EmptyBuffer_ReturnsEmpty()
    {
        var messages = await _decoder.DecodeAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task DecodeAsync_TruncatedFrame_ReturnsEmpty()
    {
        // Header declares size=38 but only 10 bytes are actually present.
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x26,  // frame size = 38 (declared)
            0x02, 0x00, 0x00, 0x00,   // doff=2, type=0, channel=0
            0x00, 0x53                 // truncated body
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task DecodeAsync_TruncatedHeader_ReturnsEmpty()
    {
        // Fewer than 8 bytes — not enough for even a frame header.
        byte[] data = [0x00, 0x00, 0x00, 0x08, 0x02];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }
}
