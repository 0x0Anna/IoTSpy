namespace IoTSpy.Protocols.Rtsp;

/// <summary>
/// Represents a decoded RTP packet header (RFC 3550 §5.1). RTP carries the negotiated
/// media stream after an RTSP SETUP/PLAY exchange, either over UDP on negotiated ports or
/// interleaved within the RTSP TCP connection via the "$" binary-data framing (RFC 2326 §10.12).
/// </summary>
public sealed class RtpPacket
{
    /// <summary>RTP version — must be 2 for RFC 3550.</summary>
    public byte Version { get; init; }

    /// <summary>Padding flag — when set, the last payload byte counts trailing padding bytes.</summary>
    public bool Padding { get; init; }

    /// <summary>Extension flag — when set, a header extension follows the CSRC list.</summary>
    public bool Extension { get; init; }

    /// <summary>Number of CSRC identifiers following the fixed header.</summary>
    public byte CsrcCount { get; init; }

    /// <summary>Marker bit — payload-format-specific (e.g. marks a frame boundary).</summary>
    public bool Marker { get; init; }

    /// <summary>Payload type (7 bits) identifying the media format (RFC 3551).</summary>
    public byte PayloadType { get; init; }

    /// <summary>16-bit sequence number, incremented by one per packet.</summary>
    public ushort SequenceNumber { get; init; }

    /// <summary>32-bit media sample timestamp.</summary>
    public uint Timestamp { get; init; }

    /// <summary>Synchronization source identifier.</summary>
    public uint Ssrc { get; init; }

    /// <summary>Contributing source identifiers (present when mixed by an RTP mixer).</summary>
    public IReadOnlyList<uint> CsrcList { get; init; } = [];

    /// <summary>Length of the payload in bytes (after the fixed header, CSRC list, and any extension).</summary>
    public int PayloadLength { get; init; }

    /// <summary>Payload bytes.</summary>
    public byte[]? Payload { get; init; }

    /// <summary>Total decoded length in bytes.</summary>
    public int TotalLength { get; init; }

    /// <summary>Raw bytes of the entire packet.</summary>
    public byte[]? RawBytes { get; init; }

    public override string ToString() =>
        $"RTP pt={PayloadType} seq={SequenceNumber} ts={Timestamp} ssrc={Ssrc:X8} len={PayloadLength}";
}

/// <summary>
/// RTSP interleaved binary data framing (RFC 2326 §10.12): a leading '$' byte, a one-byte
/// channel identifier, then a 16-bit big-endian length, followed by that many bytes of
/// embedded data (typically RTP or RTCP).
/// </summary>
public sealed record RtspInterleavedFrame(byte Channel, int Length);
