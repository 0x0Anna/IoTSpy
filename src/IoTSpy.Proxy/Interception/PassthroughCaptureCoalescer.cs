using System.Collections.Concurrent;
using System.Text.Json;
using IoTSpy.Core.Models;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Folds repeated TLS passthrough connections from one client to one host into a single capture
/// row per <see cref="Window"/>, instead of one row (and several Info log lines) per connection.
/// <para>
/// Devices that can't be intercepted often reconnect constantly — a telemetry uploader opening a
/// fresh TLS connection for every batch, dozens of times a minute. Each of those connections
/// carries the same metadata (SNI, JA3, ALPN), so recording each one separately only buries
/// everything else in the capture list. The coalesced row keeps what matters: when the traffic
/// started, how many connections there were, and how many bytes moved each way.
/// </para>
/// </summary>
public sealed class PassthroughCaptureCoalescer(TimeProvider time)
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxTrackedGroups = 10_000;

    private readonly ConcurrentDictionary<(string Client, string Host, int Port), Group> _groups = new();

    /// <summary>The passthrough connections of one client+host+port inside one window.</summary>
    public sealed class Group
    {
        private readonly object _lock = new();

        /// <summary>Serialises the database updates of this group's capture row, so a slow
        /// write of older totals can't land after a newer one.</summary>
        internal SemaphoreSlim WriteGate { get; } = new(1, 1);

        internal DateTimeOffset WindowStart { get; init; }

        /// <summary>The capture row this group's connections are folded into, once the group's
        /// first connection has recorded it.</summary>
        public Guid? CaptureId { get; private set; }

        internal bool Abandoned { get; private set; }

        private int _connections;
        private long _clientToServer;
        private long _serverToClient;
        private DateTimeOffset _firstStarted;
        private DateTimeOffset _lastEnded;

        public int Connections { get { lock (_lock) return _connections; } }

        internal void Join(DateTimeOffset started)
        {
            lock (_lock)
            {
                if (_connections == 0 || started < _firstStarted) _firstStarted = started;
                _connections++;
            }
        }

        private string _statusMessage = string.Empty;
        private TlsMetadata? _metadata;

        /// <summary>The group's first connection recorded its capture row. Later updates rewrite
        /// that row's status and metadata from these, with the totals filled in.</summary>
        public void Recorded(Guid captureId, string statusMessage, TlsMetadata metadata)
        {
            // Copy: the first connection keeps writing its own byte counts into its instance.
            var copy = JsonSerializer.Deserialize<TlsMetadata>(JsonSerializer.Serialize(metadata))!;
            lock (_lock)
            {
                CaptureId = captureId;
                _statusMessage = statusMessage;
                _metadata = copy;
            }
        }

        /// <summary>What to write to the capture row now, or null if no row was recorded. The
        /// metadata instance is the group's own; mutate it only while holding <see cref="WriteGate"/>.</summary>
        internal (Guid CaptureId, string StatusMessage, TlsMetadata Metadata, PassthroughTotals Totals)? Snapshot()
        {
            lock (_lock)
            {
                if (CaptureId is not { } id || _metadata is null) return null;
                return (id, _statusMessage, _metadata, Totals());
            }
        }

        /// <summary>The first connection couldn't record a row (upstream unreachable, DB error);
        /// the next connection for this client+host starts a fresh group and tries again.</summary>
        public void Abandon() { lock (_lock) if (CaptureId is null) Abandoned = true; }

        /// <summary>Adds a finished connection's traffic and returns the group's running totals.</summary>
        public PassthroughTotals Complete(long clientToServer, long serverToClient, DateTimeOffset ended)
        {
            lock (_lock)
            {
                _clientToServer += clientToServer;
                _serverToClient += serverToClient;
                if (ended > _lastEnded) _lastEnded = ended;
                return Totals();
            }
        }

        private PassthroughTotals Totals() => new(_connections, _clientToServer, _serverToClient,
            (long)Math.Max(0, (_lastEnded - _firstStarted).TotalMilliseconds));
    }

    /// <summary>
    /// Registers a new passthrough connection. <c>IsFirst</c> is true when it opens a new group —
    /// the caller then records the capture row (and logs at Information); later connections in the
    /// window only add their traffic to that row.
    /// </summary>
    public (Group Group, bool IsFirst) Join(string clientIp, string host, int port)
    {
        var now = time.GetUtcNow();
        if (_groups.Count >= MaxTrackedGroups) Prune(now);

        var key = (clientIp, host.Trim().TrimEnd('.').ToLowerInvariant(), port);
        while (true)
        {
            if (_groups.TryGetValue(key, out var existing) && !existing.Abandoned && now - existing.WindowStart < Window)
            {
                existing.Join(now);
                return (existing, false);
            }

            var fresh = new Group { WindowStart = now };
            fresh.Join(now);
            var won = existing is null ? _groups.TryAdd(key, fresh) : _groups.TryUpdate(key, fresh, existing);
            if (won) return (fresh, true);
            // Another connection replaced the group at the same moment — join that one instead.
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, group) in _groups)
            if (group.Abandoned || now - group.WindowStart >= Window)
                _groups.TryRemove(new KeyValuePair<(string, string, int), Group>(key, group));
    }
}

/// <summary>Running totals of a coalesced passthrough group. <c>DurationMs</c> spans from the
/// first connection's start to the latest finished connection's end.</summary>
public readonly record struct PassthroughTotals(int Connections, long ClientToServerBytes, long ServerToClientBytes, long DurationMs);
