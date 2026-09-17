namespace IoTSpy.Protocols.Amqp;

/// <summary>Which kind of AMQP 1.0 unit was decoded.</summary>
public enum AmqpFrameType
{
    /// <summary>The 8-byte protocol header exchanged at connection start ("AMQP" + id + version).</summary>
    ProtocolHeader,

    /// <summary>A regular frame (frame header + extended header + frame body).</summary>
    Frame
}

/// <summary>
/// AMQP 1.0 performative kinds, keyed by their descriptor code (0x00000000:0x0000001&lt;N&gt;)
/// per the AMQP 1.0 spec (section 2.7). <see cref="Unknown"/> and <see cref="ProtocolHeader"/>
/// are decoder-internal values, not real performatives.
/// </summary>
public enum AmqpPerformativeType
{
    /// <summary>Not a performative frame, or the descriptor was not recognized.</summary>
    Unknown = 0,

    /// <summary>Decoded unit is a protocol header, not a performative-carrying frame.</summary>
    ProtocolHeader = 1,

    Open = 0x10,
    Begin = 0x11,
    Attach = 0x12,
    Flow = 0x13,
    Transfer = 0x14,
    Disposition = 0x15,
    Detach = 0x16,
    End = 0x17,
    Close = 0x18
}
