using System.Buffers.Binary;
using System.Text;
using IoTSpy.Core.Interfaces;

namespace IoTSpy.Protocols.Amqp;

/// <summary>
/// Decodes raw bytes into AMQP 1.0 units per the OASIS AMQP 1.0 spec.
/// AMQP 1.0 runs over plain TCP (typically port 5671/5672); a connection begins with an
/// 8-byte protocol header handshake, then exchanges regular frames carrying performatives.
/// This decoder does not maintain connection state — it decodes whatever protocol header
/// and/or frames fit in the given buffer, independent of prior calls.
/// </summary>
public sealed class AmqpDecoder : IProtocolDecoder<AmqpMessage>
{
    /// <summary>
    /// Sniffs for AMQP: either the literal "AMQP" protocol header magic, or a frame header
    /// that looks structurally plausible (size &gt;= 8, doff &gt;= 2, type 0x00/0x01).
    /// </summary>
    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8) return false;

        if (IsProtocolHeaderMagic(header)) return true;

        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var doff = header[4];
        var type = header[5];
        return size >= 8 && doff >= 2 && (type == 0x00 || type == 0x01);
    }

    public Task<IReadOnlyList<AmqpMessage>> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var messages = new List<AmqpMessage>();
        var span = data.Span;
        var pos = 0;

        // A connection's very first unit may be the protocol header handshake.
        if (span.Length >= 8 && IsProtocolHeaderMagic(span))
        {
            messages.Add(new AmqpMessage
            {
                FrameType = AmqpFrameType.ProtocolHeader,
                ProtocolId = span[4],
                MajorVersion = span[5],
                MinorVersion = span[6],
                Revision = span[7],
                RawBytes = span[..8].ToArray()
            });
            pos = 8;
        }

        while (pos < span.Length)
        {
            if (!TryDecodeFrame(span, ref pos, out var frame))
                break;

            messages.Add(frame);
        }

        return Task.FromResult<IReadOnlyList<AmqpMessage>>(messages);
    }

    private static bool IsProtocolHeaderMagic(ReadOnlySpan<byte> span) =>
        span.Length >= 8 &&
        span[0] == (byte)'A' && span[1] == (byte)'M' && span[2] == (byte)'Q' && span[3] == (byte)'P' &&
        span[4] <= 3;

    private static bool TryDecodeFrame(ReadOnlySpan<byte> span, ref int pos, out AmqpMessage message)
    {
        message = null!;

        var remaining = span[pos..];
        if (remaining.Length < 8) return false;

        var size = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        if (size < 8 || size > remaining.Length) return false;

        var doff = remaining[4];
        var type = remaining[5];
        var channel = BinaryPrimitives.ReadUInt16BigEndian(remaining[6..]);

        var extHeaderLen = doff * 4 - 8;
        if (doff < 2 || extHeaderLen < 0 || 8 + extHeaderLen > size) return false;

        var bodyStart = 8 + extHeaderLen;
        var bodyLen = (int)size - bodyStart;
        var body = remaining.Slice(bodyStart, bodyLen);

        var performative = AmqpPerformativeType.Unknown;
        string? containerId = null;
        string? hostname = null;
        uint? handle = null;
        uint? deliveryId = null;

        DecodePerformative(body, ref performative, ref containerId, ref hostname, ref handle, ref deliveryId);

        message = new AmqpMessage
        {
            FrameType = AmqpFrameType.Frame,
            FrameSize = size,
            DataOffset = doff,
            TypeCode = type,
            Channel = channel,
            Performative = performative,
            ContainerId = containerId,
            Hostname = hostname,
            Handle = handle,
            DeliveryId = deliveryId,
            RawBytes = remaining[..(int)size].ToArray()
        };

        pos += (int)size;
        return true;
    }

    /// <summary>
    /// Best-effort decode of a frame body's performative descriptor and a couple of
    /// headline fields. Never throws — any unrecognized or truncated encoding simply
    /// leaves the corresponding field(s) null.
    /// </summary>
    private static void DecodePerformative(
        ReadOnlySpan<byte> body,
        ref AmqpPerformativeType performative,
        ref string? containerId,
        ref string? hostname,
        ref uint? handle,
        ref uint? deliveryId)
    {
        if (body.Length == 0) return; // e.g. an empty frame used as a keepalive

        var pos = 0;
        if (body[pos] != 0x00) return; // not a described type — leave Unknown
        pos++;

        if (!TryReadDescriptorCode(body, ref pos, out var code) || code is null) return;

        performative = code.Value switch
        {
            0x10 => AmqpPerformativeType.Open,
            0x11 => AmqpPerformativeType.Begin,
            0x12 => AmqpPerformativeType.Attach,
            0x13 => AmqpPerformativeType.Flow,
            0x14 => AmqpPerformativeType.Transfer,
            0x15 => AmqpPerformativeType.Disposition,
            0x16 => AmqpPerformativeType.Detach,
            0x17 => AmqpPerformativeType.End,
            0x18 => AmqpPerformativeType.Close,
            _ => AmqpPerformativeType.Unknown
        };

        if (performative == AmqpPerformativeType.Unknown) return;
        if (!TryEnterList(body, ref pos)) return;

        switch (performative)
        {
            case AmqpPerformativeType.Open:
                // open := list [ container-id:str, hostname:str, ... ]
                if (!TryReadStringField(body, ref pos, out containerId)) return;
                TryReadStringField(body, ref pos, out hostname);
                break;

            case AmqpPerformativeType.Transfer:
                // transfer := list [ handle:uint, delivery-id:uint, ... ]
                if (!TryReadUIntField(body, ref pos, out handle)) return;
                TryReadUIntField(body, ref pos, out deliveryId);
                break;
        }
    }

    /// <summary>
    /// Reads a described type's descriptor as a numeric ulong code (smallulong/ulong0/ulong
    /// forms). Symbol-encoded descriptors are not supported — the value is skipped and
    /// reported as unknown rather than failing the whole frame.
    /// </summary>
    private static bool TryReadDescriptorCode(ReadOnlySpan<byte> span, ref int pos, out ulong? code)
    {
        code = null;
        if (pos >= span.Length) return false;

        var ctor = span[pos];
        switch (ctor)
        {
            case 0x44: // ulong0
                pos++;
                code = 0;
                return true;

            case 0x53: // smallulong (1-byte value)
                pos++;
                if (pos >= span.Length) return false;
                code = span[pos++];
                return true;

            case 0x80: // ulong (8-byte BE value)
                pos++;
                if (pos + 8 > span.Length) return false;
                code = BinaryPrimitives.ReadUInt64BigEndian(span[pos..]);
                pos += 8;
                return true;

            default:
                // Likely a symbol descriptor — skip it, code stays unknown.
                return TrySkipValue(span, ref pos);
        }
    }

    /// <summary>
    /// Positions <paramref name="pos"/> just past a list constructor (list0/list8/list32),
    /// ready to read the list's first element.
    /// </summary>
    private static bool TryEnterList(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos >= span.Length) return false;

        var ctor = span[pos++];
        switch (ctor)
        {
            case 0x45: // list0 — zero elements
                return true;

            case 0xc0: // list8: 1-byte size, 1-byte count
                if (pos + 2 > span.Length) return false;
                pos += 2;
                return true;

            case 0xd0: // list32: 4-byte size, 4-byte count
                if (pos + 8 > span.Length) return false;
                pos += 8;
                return true;

            default:
                return false; // not a list — unsupported
        }
    }

    /// <summary>Reads an AMQP string field (str8-utf8/str32-utf8), or null/skips gracefully.</summary>
    private static bool TryReadStringField(ReadOnlySpan<byte> span, ref int pos, out string? value)
    {
        value = null;
        if (pos >= span.Length) return false;

        var ctor = span[pos];
        switch (ctor)
        {
            case 0x40: // null
                pos++;
                return true;

            case 0xa1: // str8-utf8
            {
                pos++;
                if (pos >= span.Length) return false;
                var len = span[pos++];
                if (pos + len > span.Length) return false;
                value = Encoding.UTF8.GetString(span.Slice(pos, len));
                pos += len;
                return true;
            }

            case 0xb1: // str32-utf8
            {
                pos++;
                if (pos + 4 > span.Length) return false;
                var len = (int)BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
                pos += 4;
                if (len < 0 || pos + len > span.Length) return false;
                value = Encoding.UTF8.GetString(span.Slice(pos, len));
                pos += len;
                return true;
            }

            default:
                // Unsupported encoding for this field — skip it, value stays null.
                return TrySkipValue(span, ref pos);
        }
    }

    /// <summary>Reads an AMQP uint field (uint0/smalluint/uint), or null/skips gracefully.</summary>
    private static bool TryReadUIntField(ReadOnlySpan<byte> span, ref int pos, out uint? value)
    {
        value = null;
        if (pos >= span.Length) return false;

        var ctor = span[pos];
        switch (ctor)
        {
            case 0x40: // null
                pos++;
                return true;

            case 0x43: // uint0
                pos++;
                value = 0;
                return true;

            case 0x52: // smalluint (1-byte value)
                pos++;
                if (pos >= span.Length) return false;
                value = span[pos++];
                return true;

            case 0x70: // uint (4-byte BE value)
                pos++;
                if (pos + 4 > span.Length) return false;
                value = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
                pos += 4;
                return true;

            default:
                // Unsupported encoding for this field — skip it, value stays null.
                return TrySkipValue(span, ref pos);
        }
    }

    /// <summary>
    /// Skips over one AMQP-encoded value of any kind, based on the width/size convention
    /// implied by the constructor's top nibble (per the AMQP 1.0 type system: 0x4_ = 0-width
    /// fixed, 0x5_/0x6_/0x7_/0x8_/0x9_ = 1/2/4/8/16-byte fixed, 0xa_/0xc_/0xe_ = 1-byte size
    /// prefix, 0xb_/0xd_/0xf_ = 4-byte size prefix). Used to walk past fields this decoder
    /// does not extract, without needing to fully understand their contents.
    /// </summary>
    private static bool TrySkipValue(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos >= span.Length) return false;

        var ctor = span[pos++];
        var category = ctor >> 4;

        switch (category)
        {
            case 0x4: return true; // 0-width (null, true, false, uint0, ulong0, list0)
            case 0x5: return TryAdvance(span, ref pos, 1);
            case 0x6: return TryAdvance(span, ref pos, 2);
            case 0x7: return TryAdvance(span, ref pos, 4);
            case 0x8: return TryAdvance(span, ref pos, 8);
            case 0x9: return TryAdvance(span, ref pos, 16);

            case 0xa:
            case 0xc:
            case 0xe:
            {
                if (pos >= span.Length) return false;
                var size = span[pos++];
                return TryAdvance(span, ref pos, size);
            }

            case 0xb:
            case 0xd:
            case 0xf:
            {
                if (pos + 4 > span.Length) return false;
                var size = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
                pos += 4;
                return TryAdvance(span, ref pos, (int)size);
            }

            default:
                return false; // unrecognized constructor
        }
    }

    private static bool TryAdvance(ReadOnlySpan<byte> span, ref int pos, int count)
    {
        if (count < 0 || pos + count > span.Length) return false;
        pos += count;
        return true;
    }
}
