namespace IoTSpy.Protocols.Amqp;

/// <summary>
/// Represents a decoded AMQP 1.0 unit — either the initial protocol header handshake
/// or a regular frame carrying a performative (open/begin/attach/flow/transfer/...).
/// </summary>
public sealed class AmqpMessage
{
    /// <summary>Whether this is the 8-byte protocol header handshake or a regular frame.</summary>
    public AmqpFrameType FrameType { get; init; }

    /// <summary>Protocol id from the protocol header (0=AMQP, 3=SASL). Null for regular frames.</summary>
    public byte? ProtocolId { get; init; }

    /// <summary>Major version from the protocol header. Null for regular frames.</summary>
    public byte? MajorVersion { get; init; }

    /// <summary>Minor version from the protocol header. Null for regular frames.</summary>
    public byte? MinorVersion { get; init; }

    /// <summary>Revision from the protocol header. Null for regular frames.</summary>
    public byte? Revision { get; init; }

    /// <summary>Total frame size in bytes (from the frame header). 0 for the protocol header.</summary>
    public uint FrameSize { get; init; }

    /// <summary>Data offset field (frame header words, min 2). Null for the protocol header.</summary>
    public byte? DataOffset { get; init; }

    /// <summary>Frame type byte from the frame header (0=AMQP, 1=SASL). Null for the protocol header.</summary>
    public byte? TypeCode { get; init; }

    /// <summary>Channel number the frame belongs to. Null for the protocol header.</summary>
    public ushort? Channel { get; init; }

    /// <summary>The decoded performative kind, if this is a performative-carrying frame.</summary>
    public AmqpPerformativeType Performative { get; init; } = AmqpPerformativeType.Unknown;

    /// <summary>container-id field of an `open` performative.</summary>
    public string? ContainerId { get; init; }

    /// <summary>hostname field of an `open` performative.</summary>
    public string? Hostname { get; init; }

    /// <summary>handle field of a `transfer` performative.</summary>
    public uint? Handle { get; init; }

    /// <summary>delivery-id field of a `transfer` performative.</summary>
    public uint? DeliveryId { get; init; }

    /// <summary>Raw bytes of the entire decoded unit (protocol header or frame).</summary>
    public byte[]? RawBytes { get; init; }

    public override string ToString() =>
        FrameType == AmqpFrameType.ProtocolHeader
            ? $"AMQP ProtocolHeader id={ProtocolId} v={MajorVersion}.{MinorVersion}.{Revision}"
            : $"AMQP {Performative} channel={Channel} size={FrameSize}";
}
