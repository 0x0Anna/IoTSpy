using IoTSpy.Core.Enums;

namespace IoTSpy.Core.Models;

/// <summary>
/// A flat, storage-friendly projection of a decoded MQTT or DNS(-over-HTTPS) message,
/// persisted so reports can surface protocol-level history for a device or session.
/// Deliberately does not carry the full decoder output (raw bytes, nested collections) —
/// mirrors <see cref="CapturedPacket.PayloadPreview"/>'s "truncate, don't store raw"
/// convention to keep per-message storage bounded at high message volume.
/// </summary>
public class PersistedProtocolMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null when the source IP didn't resolve to a known device.</summary>
    public Guid? DeviceId { get; set; }

    public InterceptionProtocol Protocol { get; set; }

    /// <summary>E.g. "client→broker"/"broker→client" for MQTT, "query"/"response" for DNS.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>MQTT topic, or the DNS queried name.</summary>
    public string? Subject { get; set; }

    /// <summary>Short human-readable summary line, e.g. "PUBLISH qos=1 retain=false payloadLen=234".</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Truncated payload preview, capped like <see cref="CapturedPacket.PayloadPreview"/>. Never the full raw payload.</summary>
    public string? PayloadPreview { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
