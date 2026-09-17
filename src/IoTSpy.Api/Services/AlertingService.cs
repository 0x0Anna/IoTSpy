using IoTSpy.Api.Hubs;
using IoTSpy.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IoTSpy.Api.Services;

public sealed class AlertingService : IAlertingService
{
    private const string PagerDutyEventsUrl = "https://events.pagerduty.com/v2/enqueue";

    private readonly AlertingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<CollaborationHub> _hub;
    private readonly ILogger<AlertingService> _logger;

    public AlertingService(
        IOptions<AlertingOptions> options,
        IHttpClientFactory httpClientFactory,
        IHubContext<CollaborationHub> hub,
        ILogger<AlertingService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task SendAlertAsync(string title, string body, AlertSeverity severity, CancellationToken ct = default)
    {
        if (!_options.Enabled) return;

        var tasks = new List<Task>();

        if (_options.Webhook?.Url is { Length: > 0 } webhookUrl)
            tasks.Add(SendWebhookAsync(webhookUrl, title, body, severity, ct));

        if (_options.Email is { SmtpHost: { Length: > 0 } } emailOpts)
            tasks.Add(SendEmailAsync(emailOpts, title, body, severity));

        if (_options.Slack?.WebhookUrl is { Length: > 0 })
            tasks.Add(SendSlackAsync(_options.Slack, title, body, severity, ct));

        if (_options.Teams?.WebhookUrl is { Length: > 0 })
            tasks.Add(SendTeamsAsync(_options.Teams, title, body, severity, ct));

        if (_options.PagerDuty?.IntegrationKey is { Length: > 0 })
            tasks.Add(SendPagerDutyAsync(_options.PagerDuty, title, body, severity, ct));

        if (_options.InApp)
            tasks.Add(SendInAppAsync(title, body, severity, ct));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private async Task SendInAppAsync(string title, string body, AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync("Alert", new
            {
                title,
                body,
                severity = severity.ToString(),
                timestamp = DateTimeOffset.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send in-app alert");
        }
    }

    private async Task SendWebhookAsync(string url, string title, string body, AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                title,
                body,
                severity = severity.ToString(),
                timestamp = DateTimeOffset.UtcNow
            });

            var client = _httpClientFactory.CreateClient("alerting");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (_options.Webhook?.Secret is { Length: > 0 } secret)
                request.Headers.Add("X-IoTSpy-Signature", ComputeHmac(secret, payload));

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Webhook alert returned {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send webhook alert");
        }
    }

    private async Task SendEmailAsync(EmailOptions opts, string title, string body, AlertSeverity severity)
    {
        try
        {
            using var smtp = new SmtpClient(opts.SmtpHost, opts.SmtpPort);
            if (opts.Username is { Length: > 0 })
                smtp.Credentials = new System.Net.NetworkCredential(opts.Username, opts.Password);
            smtp.EnableSsl = opts.UseSsl;

            var mail = new MailMessage(opts.From ?? "iotspy@localhost", opts.To ?? "admin@localhost")
            {
                Subject = $"[IoTSpy] [{severity}] {title}",
                Body = body,
                IsBodyHtml = false
            };

            await smtp.SendMailAsync(mail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email alert");
        }
    }

    private async Task SendSlackAsync(SlackOptions opts, string title, string body, AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            var color = severity switch
            {
                AlertSeverity.Critical => "#FF0000",
                AlertSeverity.Warning => "#FFA500",
                _ => "#36A64F"
            };

            var payload = JsonSerializer.Serialize(new
            {
                channel = opts.Channel,
                attachments = new[]
                {
                    new
                    {
                        color,
                        blocks = new object[]
                        {
                            new
                            {
                                type = "header",
                                text = new { type = "plain_text", text = $"[{severity}] {title}", emoji = true }
                            },
                            new
                            {
                                type = "section",
                                text = new { type = "mrkdwn", text = body }
                            },
                            new
                            {
                                type = "context",
                                elements = new[]
                                {
                                    new { type = "mrkdwn", text = $"*IoTSpy* · {DateTimeOffset.UtcNow:u}" }
                                }
                            }
                        }
                    }
                }
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            var client = _httpClientFactory.CreateClient("alerting");
            var response = await client.PostAsync(
                opts.WebhookUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Slack alert returned {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack alert");
        }
    }

    private async Task SendTeamsAsync(TeamsOptions opts, string title, string body, AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            var themeColor = severity switch
            {
                AlertSeverity.Critical => "FF0000",
                AlertSeverity.Warning => "FFA500",
                _ => "36A64F"
            };

            // Teams Incoming Webhook uses the legacy MessageCard format for broad connector compatibility
            var payload = JsonSerializer.Serialize(new
            {
                type = "MessageCard",
                context = "http://schema.org/extensions",
                themeColor,
                summary = title,
                sections = new[]
                {
                    new
                    {
                        activityTitle = $"**[{severity}] {title}**",
                        activitySubtitle = $"IoTSpy · {DateTimeOffset.UtcNow:u}",
                        activityText = body
                    }
                }
            });

            var client = _httpClientFactory.CreateClient("alerting");
            var response = await client.PostAsync(
                opts.WebhookUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Teams alert returned {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams alert");
        }
    }

    private async Task SendPagerDutyAsync(PagerDutyOptions opts, string title, string body, AlertSeverity severity, CancellationToken ct)
    {
        // Respect minimum severity threshold
        if (!SeverityMeetsThreshold(severity, opts.MinimumSeverity))
            return;

        try
        {
            var pdSeverity = severity switch
            {
                AlertSeverity.Critical => "critical",
                AlertSeverity.Warning => "warning",
                _ => "info"
            };

            var payload = JsonSerializer.Serialize(new
            {
                routing_key = opts.IntegrationKey,
                event_action = "trigger",
                payload = new
                {
                    summary = $"[IoTSpy] {title}",
                    severity = pdSeverity,
                    source = "iotspy",
                    timestamp = DateTimeOffset.UtcNow.ToString("o"),
                    custom_details = new { body }
                }
            });

            var client = _httpClientFactory.CreateClient("alerting");
            var response = await client.PostAsync(
                PagerDutyEventsUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("PagerDuty alert returned {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send PagerDuty alert");
        }
    }

    private static bool SeverityMeetsThreshold(AlertSeverity actual, string minimumName)
    {
        if (!Enum.TryParse<AlertSeverity>(minimumName, ignoreCase: true, out var minimum))
            minimum = AlertSeverity.Critical;
        return actual >= minimum;
    }

    private static string ComputeHmac(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
