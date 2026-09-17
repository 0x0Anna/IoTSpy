namespace IoTSpy.Protocols.MqttSn;

/// <summary>
/// Represents a decoded MQTT-SN ("MQTT for Sensor Networks") message.
/// A single shared model covers all packet types; only the fields relevant to
/// <see cref="MessageType"/> are populated, mirroring <c>IoTSpy.Protocols.Mqtt.MqttMessage</c>.
/// </summary>
public sealed class MqttSnMessage
{
    public MqttSnMessageType MessageType { get; init; }
    public int TotalLength { get; init; }

    // ── Fixed-header flags (CONNECT/PUBLISH/SUBSCRIBE/UNSUBSCRIBE/SUBACK) ────
    public bool Duplicate { get; init; }
    public MqttSnQualityOfService QoS { get; init; }
    public bool Retain { get; init; }
    public bool Will { get; init; }
    public bool CleanSession { get; init; }
    public MqttSnTopicIdType TopicIdType { get; init; }

    // ── ADVERTISE ─────────────────────────────────────────────────────────
    public byte? GwId { get; init; }
    public ushort? Duration { get; init; }

    // ── SEARCHGW ──────────────────────────────────────────────────────────
    public byte? Radius { get; init; }

    // ── GWINFO ────────────────────────────────────────────────────────────
    public string? GwAdd { get; init; }

    // ── CONNECT ───────────────────────────────────────────────────────────
    public string? ProtocolId { get; init; }
    public string? ClientId { get; init; }

    // ── CONNACK / REGACK / PUBACK / SUBACK ───────────────────────────────
    public MqttSnReturnCode? ReturnCode { get; init; }

    // ── REGISTER / REGACK ─────────────────────────────────────────────────
    public ushort? TopicId { get; init; }
    public ushort? MsgId { get; init; }
    public string? TopicName { get; init; }

    // ── PUBLISH ───────────────────────────────────────────────────────────
    public byte[]? Data { get; init; }

    /// <summary>Data decoded as UTF-8 (best-effort). Null when <see cref="Data"/> is null.</summary>
    public string? DataString => Data is null ? null : System.Text.Encoding.UTF8.GetString(Data);

    // ── SUBSCRIBE / UNSUBSCRIBE (TopicName-or-TopicId, per TopicIdType) ─────
    // TopicName is used when TopicIdType == Normal (or ShortName), TopicId otherwise
    // (PreDefined/ShortName-as-id). Both fields above are reused for these packets.

    // ── PINGREQ (optional ClientId is reused above) ──────────────────────
    // ── DISCONNECT (optional Duration is reused above) ───────────────────

    // ── Raw bytes (for replay / hex dump) ────────────────────────────────
    public byte[]? RawBytes { get; init; }

    public override string ToString() =>
        TopicName is not null
            ? $"MQTT-SN {MessageType} topic={TopicName} qos={QoS} len={Data?.Length ?? 0}"
            : TopicId is not null
                ? $"MQTT-SN {MessageType} topicId={TopicId} qos={QoS} len={Data?.Length ?? 0}"
                : $"MQTT-SN {MessageType}";
}
