namespace IoTSpy.Protocols.MqttSn;

/// <summary>
/// MQTT-SN MsgType values (1-byte field following the Length field).
/// Defined in the OASIS MQTT-SN v1.2 specification §5.2.
/// This covers the "core" subset used by real-world constrained-device traffic;
/// the full spec defines additional types (e.g. WILLTOPIC*, ENCAPSULATED) not
/// implemented here.
/// </summary>
public enum MqttSnMessageType : byte
{
    Advertise = 0x00,
    SearchGw = 0x01,
    GwInfo = 0x02,
    Connect = 0x04,
    ConnAck = 0x05,
    Register = 0x0A,
    RegAck = 0x0B,
    Publish = 0x0C,
    PubAck = 0x0D,
    Subscribe = 0x12,
    SubAck = 0x13,
    Unsubscribe = 0x14,
    UnsubAck = 0x15,
    PingReq = 0x16,
    PingResp = 0x17,
    Disconnect = 0x18
}
