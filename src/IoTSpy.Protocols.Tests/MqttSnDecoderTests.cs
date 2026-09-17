using Xunit;
using IoTSpy.Protocols.MqttSn;

namespace IoTSpy.Protocols.Tests;

public class MqttSnDecoderTests
{
    private readonly MqttSnDecoder _decoder = new();

    // ── CanDecode ────────────────────────────────────────────────────────────

    [Fact]
    public void CanDecode_TooShort_ReturnsFalse()
    {
        Assert.False(_decoder.CanDecode([0x0A]));
    }

    [Fact]
    public void CanDecode_ValidShortFormHeader_ReturnsTrue()
    {
        // Length=0x0A, MsgType=0x04 (CONNECT)
        Assert.True(_decoder.CanDecode([0x0A, 0x04]));
    }

    [Fact]
    public void CanDecode_InvalidMsgType_ReturnsFalse()
    {
        // Length=0x0A, MsgType=0x03 is not a defined MQTT-SN message type
        Assert.False(_decoder.CanDecode([0x0A, 0x03]));
    }

    [Fact]
    public void CanDecode_LengthTooSmallForHeader_ReturnsFalse()
    {
        // Declared length=1 is smaller than the 2-byte short-form header itself
        Assert.False(_decoder.CanDecode([0x01, 0x04]));
    }

    [Fact]
    public void CanDecode_ExtendedLengthHeader_ReturnsTrue()
    {
        // 0x01 marker + 2-byte length (300 = 0x012C) + MsgType=0x0C (PUBLISH)
        Assert.True(_decoder.CanDecode([0x01, 0x01, 0x2C, 0x0C]));
    }

    [Fact]
    public void CanDecode_ExtendedLengthHeaderTooShort_ReturnsFalse()
    {
        // Only 3 bytes given; extended form needs at least 4
        Assert.False(_decoder.CanDecode([0x01, 0x01, 0x2C]));
    }

    // ── DecodeAsync: CONNECT ─────────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_Connect_DecodesClientIdAndFlags()
    {
        // Length=10, MsgType=0x04 (CONNECT)
        // Flags=0x04 (Will=0, CleanSession=1)
        // ProtocolId=0x01 (MQTT-SN 1.2)
        // Duration=0x003C (60 seconds)
        // ClientId="dev1" (4 bytes)
        byte[] data =
        [
            0x0A, 0x04,
            0x04,
            0x01,
            0x00, 0x3C,
            (byte)'d', (byte)'e', (byte)'v', (byte)'1'
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.Connect, msg.MessageType);
        Assert.Equal("dev1", msg.ClientId);
        Assert.True(msg.CleanSession);
        Assert.False(msg.Will);
        Assert.Equal("MQTT-SN 1.2", msg.ProtocolId);
        Assert.Equal((ushort?)60, msg.Duration);
        Assert.Equal(10, msg.TotalLength);
    }

    // ── DecodeAsync: CONNACK ─────────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_ConnAck_DecodesReturnCode()
    {
        // Length=3, MsgType=0x05 (CONNACK), ReturnCode=0x00 (Accepted)
        byte[] data = [0x03, 0x05, 0x00];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.ConnAck, msg.MessageType);
        Assert.Equal(MqttSnReturnCode.Accepted, msg.ReturnCode);
    }

    // ── DecodeAsync: PUBLISH ─────────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_Publish_DecodesTopicIdAndData()
    {
        // Length=9, MsgType=0x0C (PUBLISH)
        // Flags=0x00 (Dup=0, QoS=0, Retain=0, TopicIdType=Normal)
        // TopicId=0x0001, MsgId=0x0002
        // Data="hi" (2 bytes)
        byte[] data =
        [
            0x09, 0x0C,
            0x00,
            0x00, 0x01,
            0x00, 0x02,
            (byte)'h', (byte)'i'
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.Publish, msg.MessageType);
        Assert.Equal((ushort?)1, msg.TopicId);
        Assert.Equal((ushort?)2, msg.MsgId);
        Assert.Equal("hi", msg.DataString);
        Assert.Equal(MqttSnQualityOfService.AtMostOnce, msg.QoS);
        Assert.Equal(MqttSnTopicIdType.Normal, msg.TopicIdType);
    }

    // ── DecodeAsync: SUBSCRIBE ───────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_Subscribe_DecodesTopicNameAndQoS()
    {
        // Length=9, MsgType=0x12 (SUBSCRIBE)
        // Flags=0x20 (QoS=1 in bits 5-6, TopicIdType=Normal)
        // MsgId=0x0001
        // TopicName="temp" (4 bytes)
        byte[] data =
        [
            0x09, 0x12,
            0x20,
            0x00, 0x01,
            (byte)'t', (byte)'e', (byte)'m', (byte)'p'
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.Subscribe, msg.MessageType);
        Assert.Equal(MqttSnQualityOfService.AtLeastOnce, msg.QoS);
        Assert.Equal((ushort?)1, msg.MsgId);
        Assert.Equal("temp", msg.TopicName);
    }

    // ── DecodeAsync: REGISTER ────────────────────────────────────────────────

    [Fact]
    public async Task DecodeAsync_Register_DecodesTopicName()
    {
        // Length=17, MsgType=0x0A (REGISTER)
        // TopicId=0x0000 (client-assigned placeholder), MsgId=0x0005
        // TopicName="sensor/temp" (11 bytes)
        byte[] data =
        [
            0x11, 0x0A,
            0x00, 0x00,
            0x00, 0x05,
            (byte)'s', (byte)'e', (byte)'n', (byte)'s', (byte)'o', (byte)'r',
            (byte)'/', (byte)'t', (byte)'e', (byte)'m', (byte)'p'
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.Register, msg.MessageType);
        Assert.Equal((ushort?)0, msg.TopicId);
        Assert.Equal((ushort?)5, msg.MsgId);
        Assert.Equal("sensor/temp", msg.TopicName);
    }

    // ── DecodeAsync: PINGREQ / PINGRESP / DISCONNECT ─────────────────────────

    [Fact]
    public async Task DecodeAsync_PingReq_ReturnsOneMessage()
    {
        // PINGREQ with no ClientId: Length=2, MsgType=0x16
        byte[] data = [0x02, 0x16];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        Assert.Equal(MqttSnMessageType.PingReq, messages[0].MessageType);
    }

    [Fact]
    public async Task DecodeAsync_PingResp_ReturnsCorrectType()
    {
        // PINGRESP: Length=2, MsgType=0x17
        byte[] data = [0x02, 0x17];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        Assert.Equal(MqttSnMessageType.PingResp, messages[0].MessageType);
    }

    [Fact]
    public async Task DecodeAsync_Disconnect_ReturnsDisconnectPacket()
    {
        // DISCONNECT with no Duration: Length=2, MsgType=0x18
        byte[] data = [0x02, 0x18];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        Assert.Equal(MqttSnMessageType.Disconnect, messages[0].MessageType);
    }

    // ── DecodeAsync: multiple packets in one buffer ──────────────────────────

    [Fact]
    public async Task DecodeAsync_TwoPackets_ReturnsBoth()
    {
        // PINGREQ (no ClientId) followed by PINGRESP
        byte[] data = [0x02, 0x16, 0x02, 0x17];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
        Assert.Equal(MqttSnMessageType.PingReq, messages[0].MessageType);
        Assert.Equal(MqttSnMessageType.PingResp, messages[1].MessageType);
    }

    // ── DecodeAsync: extended length encoding (>255 bytes) ───────────────────

    [Fact]
    public async Task DecodeAsync_ExtendedLengthPublish_DecodesLargePayload()
    {
        // PUBLISH with a 251-byte Data payload, forcing the extended-length
        // encoding (0x01 marker + 2-byte big-endian total length).
        // Header(4) + Flags(1) + TopicId(2) + MsgId(2) + Data(251) = 260 = 0x0104
        var payloadData = Enumerable.Repeat((byte)'A', 251).ToArray();
        const int totalLength = 4 + 1 + 2 + 2 + 251; // 260

        byte[] data =
        [
            0x01, (byte)(totalLength >> 8), (byte)(totalLength & 0xFF), 0x0C, // extended length + MsgType=PUBLISH
            0x00,       // Flags: Dup=0, QoS=0, Retain=0, TopicIdType=Normal
            0x00, 0x07, // TopicId=7
            0x00, 0x2A, // MsgId=42
            .. payloadData
        ];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Single(messages);
        var msg = messages[0];
        Assert.Equal(MqttSnMessageType.Publish, msg.MessageType);
        Assert.Equal(260, msg.TotalLength);
        Assert.Equal((ushort?)7, msg.TopicId);
        Assert.Equal((ushort?)42, msg.MsgId);
        Assert.Equal(251, msg.Data!.Length);
    }

    // ── DecodeAsync: insufficient / empty data ───────────────────────────────

    [Fact]
    public async Task DecodeAsync_TruncatedPacket_ReturnsEmpty()
    {
        // CONNECT declares Length=10 but only 4 bytes are actually present
        byte[] data = [0x0A, 0x04, 0x04, 0x01];

        var messages = await _decoder.DecodeAsync(data, TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task DecodeAsync_EmptyBuffer_ReturnsEmpty()
    {
        var messages = await _decoder.DecodeAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken);

        Assert.Empty(messages);
    }
}
