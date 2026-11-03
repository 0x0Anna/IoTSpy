namespace IoTSpy.Core.Enums;

public enum ManipulationRuleAction
{
    ModifyHeader,
    ModifyBody,
    OverrideStatusCode,
    Drop,
    Delay,

    /// <summary>Request phase only: rewrites the upstream target — host, port and/or
    /// path+query — using <c>RewriteHost</c>/<c>RewritePort</c>/<c>RewritePath</c>.
    /// The request line and Host header are updated and the proxy connects to the
    /// rewritten host. Rewrite targets may reference regex groups (<c>$1</c>, <c>${name}</c>)
    /// captured by <c>HostPattern</c>/<c>PathPattern</c>.</summary>
    RewriteUrl,

    /// <summary>Modifies nothing. Used as the trigger of a chain: rules whose
    /// <c>ParentRuleId</c> points at this rule fire only for exchanges this rule matched.</summary>
    MatchOnly
}
