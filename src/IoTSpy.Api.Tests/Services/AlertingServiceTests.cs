using IoTSpy.Api.Hubs;
using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Net;
using System.Text.Json;
using Xunit;

namespace IoTSpy.Api.Tests.Services;

public class AlertingServiceTests
{
    private static (AlertingService service, List<(string url, string body)> sent, IClientProxy hubAllClients) CreateService(AlertingOptions opts)
    {
        var sent = new List<(string, string)>();
        var handler = new RecordingHttpHandler(sent);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var hubAllClients = Substitute.For<IClientProxy>();
        var hubClients = Substitute.For<IHubClients>();
        hubClients.All.Returns(hubAllClients);
        var hub = Substitute.For<IHubContext<CollaborationHub>>();
        hub.Clients.Returns(hubClients);

        var service = new AlertingService(
            Options.Create(opts),
            httpClientFactory,
            hub,
            NullLogger<AlertingService>.Instance);

        return (service, sent, hubAllClients);
    }

    [Fact]
    public async Task SendAlertAsync_WhenDisabled_SendsNothing()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions { Enabled = false });
        await svc.SendAlertAsync("Title", "Body", AlertSeverity.Critical, TestContext.Current.CancellationToken);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task SendAlertAsync_SlackEnabled_SendsSlackPayload()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            Slack = new SlackOptions { WebhookUrl = "https://hooks.slack.com/test" }
        });

        await svc.SendAlertAsync("Test alert", "Details here", AlertSeverity.Warning, TestContext.Current.CancellationToken);

        Assert.Single(sent);
        Assert.Equal("https://hooks.slack.com/test", sent[0].url);

        using var doc = JsonDocument.Parse(sent[0].body);
        var attachments = doc.RootElement.GetProperty("attachments");
        Assert.Equal(1, attachments.GetArrayLength());

        // color should reflect Warning severity
        var color = attachments[0].GetProperty("color").GetString();
        Assert.Equal("#FFA500", color);
    }

    [Fact]
    public async Task SendAlertAsync_TeamsEnabled_SendsMessageCard()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            Teams = new TeamsOptions { WebhookUrl = "https://teams.example.com/webhook" }
        });

        await svc.SendAlertAsync("Teams alert", "Body text", AlertSeverity.Critical, TestContext.Current.CancellationToken);

        Assert.Single(sent);
        using var doc = JsonDocument.Parse(sent[0].body);
        Assert.Equal("MessageCard", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("FF0000", doc.RootElement.GetProperty("themeColor").GetString());
    }

    [Fact]
    public async Task SendAlertAsync_PagerDutyEnabled_SendsV2Payload()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            PagerDuty = new PagerDutyOptions { IntegrationKey = "test-key-123", MinimumSeverity = "Warning" }
        });

        await svc.SendAlertAsync("PD alert", "Body", AlertSeverity.Critical, TestContext.Current.CancellationToken);

        Assert.Single(sent);
        Assert.Contains("events.pagerduty.com", sent[0].url);

        using var doc = JsonDocument.Parse(sent[0].body);
        Assert.Equal("test-key-123", doc.RootElement.GetProperty("routing_key").GetString());
        Assert.Equal("trigger", doc.RootElement.GetProperty("event_action").GetString());
        Assert.Equal("critical", doc.RootElement.GetProperty("payload").GetProperty("severity").GetString());
    }

    [Fact]
    public async Task SendAlertAsync_PagerDuty_BelowThreshold_SkipsSend()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            PagerDuty = new PagerDutyOptions { IntegrationKey = "key", MinimumSeverity = "Critical" }
        });

        // Warning is below Critical threshold
        await svc.SendAlertAsync("Low alert", "Body", AlertSeverity.Warning, TestContext.Current.CancellationToken);

        Assert.Empty(sent);
    }

    [Fact]
    public async Task SendAlertAsync_MultipleTargets_SendsAll()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            Slack = new SlackOptions { WebhookUrl = "https://slack.example.com" },
            Teams = new TeamsOptions { WebhookUrl = "https://teams.example.com" },
        });

        await svc.SendAlertAsync("Multi", "Body", AlertSeverity.Info, TestContext.Current.CancellationToken);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task SendAlertAsync_CriticalSlack_UsesRedColor()
    {
        var (svc, sent, _) = CreateService(new AlertingOptions
        {
            Enabled = true,
            Slack = new SlackOptions { WebhookUrl = "https://slack.example.com" }
        });

        await svc.SendAlertAsync("Alert", "Body", AlertSeverity.Critical, TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(sent[0].body);
        var color = doc.RootElement.GetProperty("attachments")[0].GetProperty("color").GetString();
        Assert.Equal("#FF0000", color);
    }

    [Fact]
    public async Task SendAlertAsync_InAppEnabled_BroadcastsToAllClients()
    {
        var (svc, _, hubAllClients) = CreateService(new AlertingOptions { Enabled = true, InApp = true });

        await svc.SendAlertAsync("In-app title", "In-app body", AlertSeverity.Warning, TestContext.Current.CancellationToken);

        await hubAllClients.Received(1).SendCoreAsync(
            "Alert",
            Arg.Is<object?[]>(args => args.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAlertAsync_InAppDisabled_DoesNotBroadcast()
    {
        var (svc, _, hubAllClients) = CreateService(new AlertingOptions { Enabled = true, InApp = false });

        await svc.SendAlertAsync("Title", "Body", AlertSeverity.Warning, TestContext.Current.CancellationToken);

        await hubAllClients.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    private sealed class RecordingHttpHandler(List<(string url, string body)> recorded) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";
            recorded.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
}
