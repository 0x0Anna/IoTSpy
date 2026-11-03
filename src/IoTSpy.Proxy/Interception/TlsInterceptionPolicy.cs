using System.Collections.Concurrent;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Tls;

namespace IoTSpy.Proxy.Interception;

/// <summary>Whether to MITM a TLS connection or relay it untouched, and why.</summary>
public readonly record struct TlsInterceptionVerdict(bool Passthrough, string Reason)
{
    public static readonly TlsInterceptionVerdict Intercept = new(false, string.Empty);
    public static TlsInterceptionVerdict Bypass(string reason) => new(true, reason);
}

/// <summary>
/// Decides, from the ClientHello, whether a transparently redirected TLS connection can be
/// intercepted, and tracks clients that keep rejecting the interception certificate.
/// <para>
/// The MITM only speaks HTTP/1.1. A client whose ALPN offers no HTTP/1.x (gRPC/Firestore and other
/// h2-only clients, MQTT-over-443 with a custom ALPN, …) can't complete a handshake with it, so it
/// would fail silently and retry forever. Relaying those untouched keeps the device working and
/// still yields a metadata capture (SNI, JA3, ALPN), which identifies candidates for later full
/// interception or mocking.
/// </para>
/// </summary>
public sealed class TlsInterceptionPolicy(TimeProvider time)
{
    internal const int FailureThreshold = 3;
    internal static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AutoBypassDuration = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan FailureRecordInterval = TimeSpan.FromMinutes(1);
    private const int MaxTrackedPairs = 10_000;

    private readonly ConcurrentDictionary<(string Client, string Host), FailureState> _failures = new();

    private sealed class FailureState
    {
        public readonly object Gate = new();
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? BypassUntil;
        public DateTimeOffset? LastRecorded;
    }

    public TlsInterceptionVerdict Decide(ClientHelloInfo? hello, string host, string clientIp, ProxySettings settings)
    {
        if (hello is not null && !OffersHttp1(hello.AlpnProtocols))
            return TlsInterceptionVerdict.Bypass(
                $"client ALPN [{string.Join(", ", hello.AlpnProtocols)}] offers no HTTP/1.1 (interception is HTTP/1.1-only)");

        if (MatchesHostList(settings.TlsPassthroughHosts, host))
            return TlsInterceptionVerdict.Bypass("host is on the TLS passthrough list");

        if (settings.AutoPassthroughOnTlsFailure && IsAutoBypassed(clientIp, host))
            return TlsInterceptionVerdict.Bypass(
                $"auto: client rejected the interception certificate {FailureThreshold}+ times");

        return TlsInterceptionVerdict.Intercept;
    }

    /// <summary>No ALPN at all means the client will accept whatever the server picks — HTTP/1.1 is fine.</summary>
    internal static bool OffersHttp1(IReadOnlyList<string> alpn) =>
        alpn.Count == 0 || alpn.Any(p => p is "http/1.1" or "http/1.0");

    /// <summary>
    /// Records a failed client-side handshake. Returns true when this failure should be logged and
    /// persisted — the first one for a client+host, then at most once per <see cref="FailureRecordInterval"/>,
    /// so a device retrying several times a second doesn't flood the log or the capture table.
    /// </summary>
    public bool RecordHandshakeFailure(string clientIp, string host)
    {
        var now = time.GetUtcNow();
        if (_failures.Count >= MaxTrackedPairs) Prune(now);

        var state = _failures.GetOrAdd((clientIp, Normalize(host)), _ => new FailureState { WindowStart = now });
        lock (state.Gate)
        {
            if (now - state.WindowStart > FailureWindow)
            {
                state.WindowStart = now;
                state.Count = 0;
            }

            state.Count++;
            if (state.Count >= FailureThreshold)
                state.BypassUntil = now + AutoBypassDuration;

            if (state.LastRecorded is { } last && now - last < FailureRecordInterval)
                return false;
            state.LastRecorded = now;
            return true;
        }
    }

    public void RecordHandshakeSuccess(string clientIp, string host) =>
        _failures.TryRemove((clientIp, Normalize(host)), out _);

    /// <summary>True when this client has rejected the interception certificate for this host
    /// recently — its next connection most likely won't reach the HTTP pipeline either.</summary>
    public bool HasRecentFailures(string clientIp, string host)
    {
        if (!_failures.TryGetValue((clientIp, Normalize(host)), out var state)) return false;
        lock (state.Gate)
        {
            var now = time.GetUtcNow();
            return (state.Count > 0 && now - state.WindowStart <= FailureWindow)
                   || (state.BypassUntil is { } until && now < until);
        }
    }

    internal bool IsAutoBypassed(string clientIp, string host)
    {
        if (!_failures.TryGetValue((clientIp, Normalize(host)), out var state)) return false;
        lock (state.Gate)
            return state.BypassUntil is { } until && time.GetUtcNow() < until;
    }

    /// <summary>
    /// Matches <paramref name="host"/> against a newline/comma/whitespace-separated pattern list.
    /// <c>*.example.com</c> matches <c>example.com</c> and any subdomain; anything else is exact.
    /// Case-insensitive; a trailing dot on the host is ignored.
    /// </summary>
    public static bool MatchesHostList(string? patterns, string host)
    {
        if (string.IsNullOrWhiteSpace(patterns) || string.IsNullOrWhiteSpace(host)) return false;
        var h = Normalize(host);

        foreach (var raw in patterns.Split(['\n', '\r', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Normalize(raw);
            if (p.StartsWith("*."))
            {
                var suffix = p[1..]; // ".example.com"
                if (h.EndsWith(suffix, StringComparison.Ordinal) || h == suffix[1..]) return true;
            }
            else if (h == p)
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, state) in _failures)
        {
            lock (state.Gate)
            {
                var expired = (state.BypassUntil is null || state.BypassUntil <= now) && now - state.WindowStart > FailureWindow;
                if (expired) _failures.TryRemove(key, out _);
            }
        }
    }
}
