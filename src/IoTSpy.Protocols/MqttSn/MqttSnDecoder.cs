using System.Buffers.Binary;
using System.Text;
using IoTSpy.Core.Interfaces;

namespace IoTSpy.Protocols.MqttSn;

/// <summary>
/// Decodes raw bytes into MQTT-SN ("MQTT for Sensor Networks") messages, per the
/// OASIS MQTT-SN v1.2 specification. MQTT-SN is a UDP-friendly binary variant of MQTT
/// designed for constrained devices; each message is self-delimiting via a leading
/// Length field rather than MQTT's type-nibble-plus-remaining-length fixed header.
/// The decoder is stateless; each call to <see cref="DecodeAsync"/> is independent.
/// This is decoder-only: no gateway/UDP transport is implemented here.
/// </summary>
public sealed class MqttSnDecoder : IProtocolDecoder<MqttSnMessage>
{
    /// <summary>
    /// MQTT-SN messages start with either a 1-byte Length (1-255, for short messages)
    /// or 0x01 followed by a 2-byte big-endian extended Length (for messages >255 bytes),
    /// then a 1-byte MsgType. We sniff by validating the MsgType is one of the defined
    /// codes and that the declared length is at least the header size it implies.
    /// </summary>
    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2) return false;

        int headerSize;
        byte msgType;

        if (header[0] == 0x01)
        {
            // Extended length form: 0x01, lenHi, lenLo, msgType
            if (header.Length < 4) return false;
            var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);
            if (declaredLength < 4) return false;
            headerSize = 4;
            msgType = header[3];
        }
        else
        {
            var declaredLength = header[0];
            if (declaredLength < 2) return false;
            headerSize = 2;
            msgType = header[1];
        }

        if (header.Length < headerSize) return false;

        return Enum.IsDefined(typeof(MqttSnMessageType), msgType);
    }

    public Task<IReadOnlyList<MqttSnMessage>> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var messages = new List<MqttSnMessage>();
        var span = data.Span;
        var offset = 0;

        while (offset < span.Length && !ct.IsCancellationRequested)
        {
            if (!TryDecodePacket(span[offset..], out var msg, out var consumed))
                break;

            messages.Add(msg);
            offset += consumed;
        }

        return Task.FromResult<IReadOnlyList<MqttSnMessage>>(messages);
    }

    private static bool TryDecodePacket(ReadOnlySpan<byte> span, out MqttSnMessage message, out int consumed)
    {
        message = default!;
        consumed = 0;

        if (span.Length < 2) return false;

        int totalLength;
        int headerSize;

        if (span[0] == 0x01)
        {
            if (span.Length < 4) return false;
            totalLength = BinaryPrimitives.ReadUInt16BigEndian(span[1..]);
            headerSize = 4;
        }
        else
        {
            totalLength = span[0];
            headerSize = 2;
        }

        if (totalLength < headerSize) return false;
        if (span.Length < totalLength) return false;

        var msgTypeByte = span[headerSize - 1];
        if (!Enum.IsDefined(typeof(MqttSnMessageType), msgTypeByte)) return false;
        var msgType = (MqttSnMessageType)msgTypeByte;

        var payload = span[headerSize..totalLength];
        var rawBytes = span[..totalLength].ToArray();

        message = msgType switch
        {
            MqttSnMessageType.Advertise => DecodeAdvertise(payload, rawBytes),
            MqttSnMessageType.SearchGw => DecodeSearchGw(payload, rawBytes),
            MqttSnMessageType.GwInfo => DecodeGwInfo(payload, rawBytes),
            MqttSnMessageType.Connect => DecodeConnect(payload, rawBytes),
            MqttSnMessageType.ConnAck => DecodeConnAck(payload, rawBytes),
            MqttSnMessageType.Register => DecodeRegister(payload, rawBytes),
            MqttSnMessageType.RegAck => DecodeRegAck(payload, rawBytes),
            MqttSnMessageType.Publish => DecodePublish(payload, rawBytes),
            MqttSnMessageType.PubAck => DecodePubAck(payload, rawBytes),
            MqttSnMessageType.Subscribe => DecodeSubscribe(payload, rawBytes),
            MqttSnMessageType.SubAck => DecodeSubAck(payload, rawBytes),
            MqttSnMessageType.Unsubscribe => DecodeUnsubscribe(payload, rawBytes),
            MqttSnMessageType.UnsubAck => DecodeUnsubAck(payload, rawBytes),
            MqttSnMessageType.PingReq => DecodePingReq(payload, rawBytes),
            MqttSnMessageType.PingResp => new MqttSnMessage
            {
                MessageType = MqttSnMessageType.PingResp,
                TotalLength = totalLength,
                RawBytes = rawBytes
            },
            MqttSnMessageType.Disconnect => DecodeDisconnect(payload, rawBytes),
            _ => new MqttSnMessage
            {
                MessageType = msgType,
                TotalLength = totalLength,
                RawBytes = rawBytes
            }
        };

        consumed = totalLength;
        return true;
    }

    // ── ADVERTISE ────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeAdvertise(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 3)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Advertise, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var gwId = payload[0];
        var duration = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Advertise,
            TotalLength = rawBytes.Length,
            GwId = gwId,
            Duration = duration,
            RawBytes = rawBytes
        };
    }

    // ── SEARCHGW ─────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeSearchGw(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        byte? radius = payload.Length >= 1 ? payload[0] : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.SearchGw,
            TotalLength = rawBytes.Length,
            Radius = radius,
            RawBytes = rawBytes
        };
    }

    // ── GWINFO ───────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeGwInfo(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 1)
            return new MqttSnMessage { MessageType = MqttSnMessageType.GwInfo, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var gwId = payload[0];
        string? gwAdd = payload.Length > 1
            ? Encoding.UTF8.GetString(payload[1..])
            : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.GwInfo,
            TotalLength = rawBytes.Length,
            GwId = gwId,
            GwAdd = gwAdd,
            RawBytes = rawBytes
        };
    }

    // ── CONNECT ──────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeConnect(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 4)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Connect, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var flags = payload[pos++];
        var will = (flags & 0x08) != 0;
        var cleanSession = (flags & 0x04) != 0;

        var protocolIdByte = payload[pos++];
        var protocolId = protocolIdByte == 0x01 ? "MQTT-SN 1.2" : protocolIdByte.ToString();

        var duration = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;

        var clientId = pos < payload.Length ? Encoding.UTF8.GetString(payload[pos..]) : string.Empty;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Connect,
            TotalLength = rawBytes.Length,
            Will = will,
            CleanSession = cleanSession,
            ProtocolId = protocolId,
            Duration = duration,
            ClientId = clientId,
            RawBytes = rawBytes
        };
    }

    // ── CONNACK ──────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeConnAck(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        MqttSnReturnCode? returnCode = payload.Length >= 1 ? (MqttSnReturnCode)payload[0] : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.ConnAck,
            TotalLength = rawBytes.Length,
            ReturnCode = returnCode,
            RawBytes = rawBytes
        };
    }

    // ── REGISTER ─────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeRegister(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 4)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Register, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var topicId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var topicName = pos < payload.Length ? Encoding.UTF8.GetString(payload[pos..]) : string.Empty;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Register,
            TotalLength = rawBytes.Length,
            TopicId = topicId,
            MsgId = msgId,
            TopicName = topicName,
            RawBytes = rawBytes
        };
    }

    // ── REGACK ───────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeRegAck(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 5)
            return new MqttSnMessage { MessageType = MqttSnMessageType.RegAck, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var topicId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var returnCode = (MqttSnReturnCode)payload[pos];

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.RegAck,
            TotalLength = rawBytes.Length,
            TopicId = topicId,
            MsgId = msgId,
            ReturnCode = returnCode,
            RawBytes = rawBytes
        };
    }

    // ── PUBLISH ──────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodePublish(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 5)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Publish, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var flags = payload[pos++];
        var (dup, qos, retain, topicIdType) = DecodeFlags(flags);

        var topicId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;

        var appData = payload[pos..].ToArray();

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Publish,
            TotalLength = rawBytes.Length,
            Duplicate = dup,
            QoS = qos,
            Retain = retain,
            TopicIdType = topicIdType,
            TopicId = topicId,
            MsgId = msgId,
            Data = appData,
            RawBytes = rawBytes
        };
    }

    // ── PUBACK ───────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodePubAck(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 5)
            return new MqttSnMessage { MessageType = MqttSnMessageType.PubAck, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var topicId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var returnCode = (MqttSnReturnCode)payload[pos];

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.PubAck,
            TotalLength = rawBytes.Length,
            TopicId = topicId,
            MsgId = msgId,
            ReturnCode = returnCode,
            RawBytes = rawBytes
        };
    }

    // ── SUBSCRIBE ────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeSubscribe(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 3)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Subscribe, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var flags = payload[pos++];
        var (dup, qos, _, topicIdType) = DecodeFlags(flags);

        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;

        var rest = payload[pos..];
        string? topicName = null;
        ushort? topicId = null;
        if (topicIdType == MqttSnTopicIdType.PreDefined)
        {
            if (rest.Length >= 2) topicId = BinaryPrimitives.ReadUInt16BigEndian(rest);
        }
        else
        {
            topicName = Encoding.UTF8.GetString(rest);
        }

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Subscribe,
            TotalLength = rawBytes.Length,
            Duplicate = dup,
            QoS = qos,
            TopicIdType = topicIdType,
            MsgId = msgId,
            TopicName = topicName,
            TopicId = topicId,
            RawBytes = rawBytes
        };
    }

    // ── SUBACK ───────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeSubAck(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 6)
            return new MqttSnMessage { MessageType = MqttSnMessageType.SubAck, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var flags = payload[pos++];
        var (_, qos, _, _) = DecodeFlags(flags);

        var topicId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;
        var returnCode = (MqttSnReturnCode)payload[pos];

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.SubAck,
            TotalLength = rawBytes.Length,
            QoS = qos,
            TopicId = topicId,
            MsgId = msgId,
            ReturnCode = returnCode,
            RawBytes = rawBytes
        };
    }

    // ── UNSUBSCRIBE ──────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeUnsubscribe(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        if (payload.Length < 3)
            return new MqttSnMessage { MessageType = MqttSnMessageType.Unsubscribe, TotalLength = rawBytes.Length, RawBytes = rawBytes };

        var pos = 0;
        var flags = payload[pos++];
        var (_, _, _, topicIdType) = DecodeFlags(flags);

        var msgId = BinaryPrimitives.ReadUInt16BigEndian(payload[pos..]);
        pos += 2;

        var rest = payload[pos..];
        string? topicName = null;
        ushort? topicId = null;
        if (topicIdType == MqttSnTopicIdType.PreDefined)
        {
            if (rest.Length >= 2) topicId = BinaryPrimitives.ReadUInt16BigEndian(rest);
        }
        else
        {
            topicName = Encoding.UTF8.GetString(rest);
        }

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Unsubscribe,
            TotalLength = rawBytes.Length,
            TopicIdType = topicIdType,
            MsgId = msgId,
            TopicName = topicName,
            TopicId = topicId,
            RawBytes = rawBytes
        };
    }

    // ── UNSUBACK ─────────────────────────────────────────────────────────────

    private static MqttSnMessage DecodeUnsubAck(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        ushort? msgId = payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.UnsubAck,
            TotalLength = rawBytes.Length,
            MsgId = msgId,
            RawBytes = rawBytes
        };
    }

    // ── PINGREQ (optional ClientId) ───────────────────────────────────────────

    private static MqttSnMessage DecodePingReq(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        var clientId = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.PingReq,
            TotalLength = rawBytes.Length,
            ClientId = clientId,
            RawBytes = rawBytes
        };
    }

    // ── DISCONNECT (optional Duration) ───────────────────────────────────────

    private static MqttSnMessage DecodeDisconnect(ReadOnlySpan<byte> payload, byte[] rawBytes)
    {
        ushort? duration = payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : null;

        return new MqttSnMessage
        {
            MessageType = MqttSnMessageType.Disconnect,
            TotalLength = rawBytes.Length,
            Duration = duration,
            RawBytes = rawBytes
        };
    }

    // ── Wire-format helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Decodes the shared MQTT-SN Flags byte: DUP(0x80), QoS(0x60, 2 bits, signed),
    /// Retain(0x10), Will(0x08), CleanSession(0x04), TopicIdType(0x03, 2 bits).
    /// </summary>
    private static (bool Dup, MqttSnQualityOfService QoS, bool Retain, MqttSnTopicIdType TopicIdType) DecodeFlags(byte flags)
    {
        var dup = (flags & 0x80) != 0;
        var qosBits = (flags >> 5) & 0x03;
        var qos = qosBits == 0b11 ? MqttSnQualityOfService.MinusOne : (MqttSnQualityOfService)qosBits;
        var retain = (flags & 0x10) != 0;
        var topicIdType = (MqttSnTopicIdType)(flags & 0x03);

        return (dup, qos, retain, topicIdType);
    }
}
