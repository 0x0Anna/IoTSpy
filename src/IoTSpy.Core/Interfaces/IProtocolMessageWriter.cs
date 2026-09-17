using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

/// <summary>
/// Accepts decoded MQTT/DNS(-over-HTTPS) messages from the proxy hot path and persists
/// them to the database in batches. Mirrors <see cref="ICaptureBatchWriter"/>'s deferred-
/// persistence pattern for the same reason: avoid one DB round-trip per message.
/// Implemented by <c>ProtocolMessageBatchWriter</c> in the API layer.
/// </summary>
public interface IProtocolMessageWriter
{
    /// <summary>
    /// Enqueues a message for deferred persistence. Never blocks the proxy hot path —
    /// under sustained overload the oldest buffered message is dropped to make room.
    /// </summary>
    bool TryEnqueue(PersistedProtocolMessage message);
}
