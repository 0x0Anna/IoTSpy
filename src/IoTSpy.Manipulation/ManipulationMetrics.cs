using Prometheus;

namespace IoTSpy.Manipulation;

/// <summary>
/// Prometheus metrics for the manipulation pipeline (rules, breakpoints, fuzzer).
/// Lives here (rather than IoTSpy.Api.Services.IoTSpyMetrics) because IoTSpy.Manipulation
/// is a lower-level project that IoTSpy.Api depends on, not the reverse — the metric
/// collectors register into prometheus-net's shared default registry regardless of which
/// assembly creates them, so they still surface on the same /metrics endpoint as the
/// rest of IoTSpyMetrics.
/// </summary>
public static class ManipulationMetrics
{
    private static readonly Counter BreakpointHits = Metrics.CreateCounter(
        "iotspy_breakpoint_hits_total",
        "Total times a scripted breakpoint matched and fired",
        labelNames: ["breakpoint_id"]);

    private static readonly Counter RuleMatches = Metrics.CreateCounter(
        "iotspy_rule_matches_total",
        "Total times a manipulation rule matched a proxied request",
        labelNames: ["rule_id"]);

    private static readonly Counter FuzzerRequests = Metrics.CreateCounter(
        "iotspy_fuzzer_requests_total",
        "Total fuzzer mutation requests sent, by outcome",
        labelNames: ["status"]);

    public static void RecordBreakpointHit(Guid breakpointId) =>
        BreakpointHits.WithLabels(breakpointId.ToString()).Inc();

    public static void RecordRuleMatch(Guid ruleId) =>
        RuleMatches.WithLabels(ruleId.ToString()).Inc();

    public static void RecordFuzzerRequest(string status) =>
        FuzzerRequests.WithLabels(status).Inc();
}
