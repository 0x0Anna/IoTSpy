namespace IoTSpy.Protocols.MqttSn;

/// <summary>
/// MQTT-SN ReturnCode values used in CONNACK, REGACK, PUBACK, SUBACK.
/// Defined in the OASIS MQTT-SN v1.2 specification §5.3.12.
/// </summary>
public enum MqttSnReturnCode : byte
{
    Accepted = 0x00,
    RejectedCongestion = 0x01,
    RejectedInvalidTopicId = 0x02,
    RejectedNotSupported = 0x03
}
