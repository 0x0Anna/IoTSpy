namespace IoTSpy.Core.Models;

/// <summary>
/// The manipulation rules that apply to a whole connection (matched as <c>CONNECT host:port</c>,
/// path <c>/</c>): request-phase Delay rules in priority order, up to the first matching Drop rule.
/// A Delay at a higher priority than a Drop therefore holds the connection before it is closed.
/// </summary>
public sealed record ConnectionRuleMatch(IReadOnlyList<ManipulationRule> Delays, ManipulationRule? Drop)
{
    public static readonly ConnectionRuleMatch None = new([], null);

    public int TotalDelayMs => Delays.Sum(d => Math.Max(0, d.DelayMs ?? 0));
    public bool IsEmpty => Drop is null && Delays.Count == 0;
}
