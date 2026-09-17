namespace IoTSpy.Protocols.MqttSn;

/// <summary>
/// MQTT-SN QoS levels, encoded as a 2-bit field in the Flags byte.
/// MQTT-SN additionally defines QoS -1 ("QoS level -1", 0b11) for pre-registered
/// topics published without a prior CONNECT — represented here as <see cref="MinusOne"/>.
/// </summary>
public enum MqttSnQualityOfService : sbyte
{
    AtMostOnce = 0,
    AtLeastOnce = 1,
    ExactlyOnce = 2,
    MinusOne = -1
}

/// <summary>
/// MQTT-SN TopicIdType, encoded as a 2-bit field in the Flags byte.
/// Determines how the TopicId field of PUBLISH/SUBSCRIBE/UNSUBSCRIBE is interpreted.
/// </summary>
public enum MqttSnTopicIdType : byte
{
    Normal = 0b00,
    PreDefined = 0b01,
    ShortName = 0b10
}
