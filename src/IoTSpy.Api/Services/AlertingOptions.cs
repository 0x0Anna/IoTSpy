namespace IoTSpy.Api.Services;

public class AlertingOptions
{
    public const string SectionName = "Alerting";

    public bool Enabled { get; set; } = false;
    public WebhookOptions? Webhook { get; set; }
    public EmailOptions? Email { get; set; }
    public SlackOptions? Slack { get; set; }
    public TeamsOptions? Teams { get; set; }
    public PagerDutyOptions? PagerDuty { get; set; }

    /// <summary>Pushes alerts to connected clients via the existing collaboration SignalR
    /// hub (Clients.All — not session-scoped, since alerts aren't tied to an investigation
    /// session). Needs no external config, unlike the other channels.</summary>
    public bool InApp { get; set; } = false;
}

public class WebhookOptions
{
    public string? Url { get; set; }
    public string? Secret { get; set; }
}

public class EmailOptions
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class SlackOptions
{
    /// <summary>Slack Incoming Webhook URL from api.slack.com/apps.</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>Optional channel override (e.g. #alerts). Leave blank to use the webhook default.</summary>
    public string? Channel { get; set; }
}

public class TeamsOptions
{
    /// <summary>Microsoft Teams Incoming Webhook connector URL.</summary>
    public string? WebhookUrl { get; set; }
}

public class PagerDutyOptions
{
    /// <summary>PagerDuty Events API v2 integration key (routing_key).</summary>
    public string? IntegrationKey { get; set; }
    /// <summary>Minimum severity that triggers a PagerDuty incident. Defaults to Critical.</summary>
    public string MinimumSeverity { get; set; } = "Critical";
}
