using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;

namespace IoTSpy.Core.Interfaces;

/// <summary>
/// Applies manipulation rules and breakpoint scripts to HTTP messages flowing through the proxy.
/// Called by the proxy interception pipeline.
/// </summary>
public interface IManipulationService
{
    /// <summary>
    /// Apply all enabled rules and scripts to the given message at the specified phase.
    /// Returns true if any modification was made.
    /// </summary>
    Task<bool> ApplyAsync(HttpMessage message, ManipulationPhase phase, CancellationToken ct = default);

    /// <summary>
    /// Connection-level rule check for traffic the HTTP pipeline never sees — TLS that is relayed
    /// untouched (passthrough) or whose handshake fails. Matches enabled request-phase Drop and
    /// Delay rules against the connection as <c>CONNECT host:port</c> with path <c>/</c>, so a rule
    /// with only a host pattern (or a method pattern of <c>CONNECT</c>) applies to the connection.
    /// Returns the matching Delay rules in priority order up to the first matching Drop rule.
    /// </summary>
    Task<ConnectionRuleMatch> MatchConnectionRulesAsync(string host, int port, CancellationToken ct = default);

    /// <summary>
    /// Replay a captured request (optionally modified) and return the session with the response.
    /// </summary>
    Task<ReplaySession> ReplayAsync(ReplaySession session, CancellationToken ct = default);

    /// <summary>
    /// Start a fuzzer job against a captured request.
    /// </summary>
    Task<FuzzerJob> StartFuzzerAsync(FuzzerJob job, CancellationToken ct = default);

    /// <summary>
    /// Cancel a running fuzzer job.
    /// </summary>
    Task CancelFuzzerAsync(Guid jobId);

    /// <summary>
    /// Check if a fuzzer job is running.
    /// </summary>
    bool IsFuzzerRunning(Guid jobId);
}
