namespace IoTSpy.Core.Enums;

public enum InterceptionProtocol
{
    Http,
    Https,
    Mqtt,
    MqttTls,
    MqttSn,
    CoAP,
    Dns,
    MDns,
    WebSocket,
    WebSocketTls,
    Grpc,
    Modbus,
    Rtsp,
    Rtp,
    Amqp,
    TlsPassthrough,

    /// <summary>
    /// HTTPS traffic identified as DNS-over-HTTPS (RFC 8484) — the request is otherwise a
    /// normal HTTPS capture, but its framing (path/content-type/query param) indicates it
    /// is carrying an encrypted DNS query/response rather than application content.
    /// </summary>
    DohDetected,

    Other
}
