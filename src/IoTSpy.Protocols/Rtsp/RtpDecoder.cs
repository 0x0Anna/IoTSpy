using System.Buffers.Binary;
using IoTSpy.Core.Interfaces;

namespace IoTSpy.Protocols.Rtsp;

/// <summary>
/// Decodes raw bytes into RTP packets per RFC 3550 §5.1. RTP is a binary media-transport
/// protocol negotiated by RTSP, typically carried over UDP on negotiated ports, or
/// interleaved within the RTSP TCP connection via the "$" binary-data framing (RFC 2326 §10.12).
/// </summary>
public sealed class RtpDecoder : IProtocolDecoder<RtpPacket>
{
    private const int FixedHeaderLength = 12;

    /// <summary>
    /// Sniffs for RTP: either a raw 12-byte fixed header with version == 2, or RTSP's
    /// interleaved binary data marker ('$' + channel + 16-bit length) wrapping an RTP header.
    /// </summary>
    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == (byte)'$')
        {
            // Interleaved framing: "$" + channel (1 byte) + length (2 bytes, big-endian),
            // then the embedded RTP/RTCP data.
            if (header.Length < 4 + FixedHeaderLength) return header.Length >= 4;
            var version = (header[4] >> 6) & 0x03;
            return version == 2;
        }

        if (header.Length < FixedHeaderLength) return false;
        var v = (header[0] >> 6) & 0x03;
        return v == 2;
    }

    public Task<IReadOnlyList<RtpPacket>> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var packets = new List<RtpPacket>();
        var span = data.Span;

        // Strip RTSP interleaved framing if present.
        if (span.Length >= 4 && span[0] == (byte)'$')
        {
            var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
            var available = span.Length - 4;
            var take = Math.Min(declaredLength, available);
            span = span.Slice(4, take);
        }

        if (TryDecode(span, out var packet))
            packets.Add(packet);

        return Task.FromResult<IReadOnlyList<RtpPacket>>(packets);
    }

    private static bool TryDecode(ReadOnlySpan<byte> span, out RtpPacket packet)
    {
        packet = default!;

        if (span.Length < FixedHeaderLength) return false;

        var byte0 = span[0];
        var version = (byte)((byte0 >> 6) & 0x03);
        if (version != 2) return false;

        var padding = (byte0 & 0x20) != 0;
        var extension = (byte0 & 0x10) != 0;
        var csrcCount = (byte)(byte0 & 0x0F);

        var byte1 = span[1];
        var marker = (byte1 & 0x80) != 0;
        var payloadType = (byte)(byte1 & 0x7F);

        var sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(span[8..12]);

        var pos = FixedHeaderLength;

        var csrcListLength = csrcCount * 4;
        if (pos + csrcListLength > span.Length) return false;
        var csrcList = new List<uint>(csrcCount);
        for (var i = 0; i < csrcCount; i++)
        {
            csrcList.Add(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos, 4)));
            pos += 4;
        }

        if (extension)
        {
            if (pos + 4 > span.Length) return false;
            var extLengthWords = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos + 2, 2));
            var extTotalLength = 4 + extLengthWords * 4;
            if (pos + extTotalLength > span.Length) return false;
            pos += extTotalLength;
        }

        var payload = pos < span.Length ? span[pos..].ToArray() : [];

        packet = new RtpPacket
        {
            Version = version,
            Padding = padding,
            Extension = extension,
            CsrcCount = csrcCount,
            Marker = marker,
            PayloadType = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
            CsrcList = csrcList,
            PayloadLength = payload.Length,
            Payload = payload,
            TotalLength = span.Length,
            RawBytes = span.ToArray()
        };

        return true;
    }
}
