using IoTSpy.Core.Enums;

namespace IoTSpy.Core.Models;

public class ManipulationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } // lower = higher priority

    // Match criteria (all optional; null = match all)
    public string? HostPattern { get; set; }   // regex
    public string? PathPattern { get; set; }   // regex
    public string? MethodPattern { get; set; } // exact or regex

    // When to apply
    public ManipulationPhase Phase { get; set; } = ManipulationPhase.Request;

    // Actions (JSON; multiple actions per rule)
    public ManipulationRuleAction Action { get; set; }

    // Action parameters
    public string? HeaderName { get; set; }     // for ModifyHeader
    public string? HeaderValue { get; set; }    // for ModifyHeader (null = remove header)
    public string? BodyReplace { get; set; }    // replacement body or regex substitution
    public string? BodyReplaceWith { get; set; }
    public int? OverrideStatusCode { get; set; }
    public int? DelayMs { get; set; }           // for Delay action

    /// <summary>When true, fires an alert (via IAlertingService) each time this rule matches.
    /// Opt-in and defaults false — rules match on every proxied request, so alerting
    /// unconditionally would flood external channels (webhook/email/Slack/PagerDuty) on
    /// routine traffic.</summary>
    public bool AlertOnMatch { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
