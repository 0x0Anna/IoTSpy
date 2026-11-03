using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Manipulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Manipulation.Tests;

public class ManipulationServiceTests
{
    private static IServiceScopeFactory MakeScopeFactory(IBreakpointRepository breakpoints)
    {
        var services = new ServiceCollection();
        services.AddSingleton(breakpoints);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static ManipulationService MakeService(
        IManipulationRuleCache ruleCache,
        IBreakpointRepository breakpoints,
        IAlertingService alerting)
    {
        var apiSpecService = Substitute.For<IApiSpecService>();
        apiSpecService.ApplyMockAsync(Arg.Any<HttpMessage>(), Arg.Any<ManipulationPhase>(), Arg.Any<CancellationToken>())
            .Returns(false);

        return new ManipulationService(
            new RulesEngine(NullLogger<RulesEngine>.Instance),
            new CSharpScriptEngine(NullLogger<CSharpScriptEngine>.Instance),
            new JavaScriptEngine(NullLogger<JavaScriptEngine>.Instance),
            new ReplayService(Substitute.For<IHttpClientFactory>(), NullLogger<ReplayService>.Instance),
            new FuzzerService(Substitute.For<IHttpClientFactory>(), NullLogger<FuzzerService>.Instance),
            apiSpecService,
            ruleCache,
            MakeScopeFactory(breakpoints),
            alerting,
            NullLogger<ManipulationService>.Instance);
    }

    private static IManipulationRuleCache MakeRuleCache(params ManipulationRule[] rules)
    {
        var cache = Substitute.For<IManipulationRuleCache>();
        cache.GetEnabledAsync(Arg.Any<CancellationToken>()).Returns(rules.ToList());
        return cache;
    }

    private static IBreakpointRepository MakeBreakpointRepo(params Breakpoint[] breakpoints)
    {
        var repo = Substitute.For<IBreakpointRepository>();
        repo.GetEnabledAsync(Arg.Any<CancellationToken>()).Returns(breakpoints.ToList());
        return repo;
    }

    private static HttpMessage MakeMessage() => new()
    {
        Host = "device.local",
        Path = "/api/test",
        Method = "GET",
        RequestHeaders = "",
        RequestBody = "",
        ResponseHeaders = "",
        ResponseBody = ""
    };

    [Fact]
    public async Task ApplyAsync_RuleMatchesWithAlertOnMatchTrue_SendsAlert()
    {
        var rule = new ManipulationRule { Name = "alert-rule", Phase = ManipulationPhase.Request, AlertOnMatch = true, Action = ManipulationRuleAction.Delay };
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), alerting);

        await svc.ApplyAsync(MakeMessage(), ManipulationPhase.Request, TestContext.Current.CancellationToken);

        await alerting.Received(1).SendAlertAsync(
            Arg.Is<string>(t => t.Contains("alert-rule")),
            Arg.Any<string>(),
            Arg.Any<AlertSeverity>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_RuleMatchesWithAlertOnMatchFalse_DoesNotSendAlert()
    {
        var rule = new ManipulationRule { Name = "quiet-rule", Phase = ManipulationPhase.Request, AlertOnMatch = false, Action = ManipulationRuleAction.Delay };
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), alerting);

        await svc.ApplyAsync(MakeMessage(), ManipulationPhase.Request, TestContext.Current.CancellationToken);

        await alerting.DidNotReceive().SendAlertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AlertSeverity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_RuleAlertOnMatchTrueButHostDoesNotMatch_DoesNotSendAlert()
    {
        var rule = new ManipulationRule
        {
            Name = "scoped-rule", Phase = ManipulationPhase.Request, AlertOnMatch = true,
            Action = ManipulationRuleAction.Delay, HostPattern = "other\\.example\\.com"
        };
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), alerting);

        await svc.ApplyAsync(MakeMessage(), ManipulationPhase.Request, TestContext.Current.CancellationToken);

        await alerting.DidNotReceive().SendAlertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AlertSeverity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_BreakpointExecutesWithAlertOnMatchTrue_SendsAlert()
    {
        var bp = new Breakpoint
        {
            Name = "alert-bp", Phase = ManipulationPhase.Request, AlertOnMatch = true,
            Language = ScriptLanguage.JavaScript, ScriptCode = "modified = false;"
        };
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(), MakeBreakpointRepo(bp), alerting);

        await svc.ApplyAsync(MakeMessage(), ManipulationPhase.Request, TestContext.Current.CancellationToken);

        await alerting.Received(1).SendAlertAsync(
            Arg.Is<string>(t => t.Contains("alert-bp")),
            Arg.Any<string>(),
            Arg.Any<AlertSeverity>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_BreakpointExecutesWithAlertOnMatchFalse_DoesNotSendAlert()
    {
        var bp = new Breakpoint
        {
            Name = "quiet-bp", Phase = ManipulationPhase.Request, AlertOnMatch = false,
            Language = ScriptLanguage.JavaScript, ScriptCode = "modified = false;"
        };
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(), MakeBreakpointRepo(bp), alerting);

        await svc.ApplyAsync(MakeMessage(), ManipulationPhase.Request, TestContext.Current.CancellationToken);

        await alerting.DidNotReceive().SendAlertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AlertSeverity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_AlertingThrows_DoesNotPropagateAndRuleStillApplies()
    {
        var rule = new ManipulationRule
        {
            Name = "alert-rule", Phase = ManipulationPhase.Request, AlertOnMatch = true,
            Action = ManipulationRuleAction.ModifyHeader, HeaderName = "X-Test", HeaderValue = "1"
        };
        var alerting = Substitute.For<IAlertingService>();
        alerting.SendAlertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AlertSeverity>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("alert channel down"));
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), alerting);

        var message = MakeMessage();
        var modified = await svc.ApplyAsync(message, ManipulationPhase.Request, TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Contains("X-Test: 1", message.RequestHeaders);
    }

    // ── Connection-level Drop/Delay rules (TLS passthrough / failed handshakes) ────

    private static ManipulationRule ConnRule(string name, string? host = null, string? method = null, string? path = null,
        ManipulationPhase phase = ManipulationPhase.Request, ManipulationRuleAction action = ManipulationRuleAction.Drop,
        int? delayMs = null, int priority = 0) =>
        new() { Name = name, HostPattern = host, MethodPattern = method, PathPattern = path, Phase = phase, Action = action, DelayMs = delayMs, Priority = priority };

    private static Task<ConnectionRuleMatch> MatchTelemetry(ManipulationService svc) =>
        svc.MatchConnectionRulesAsync("telemetry.example.net", 443, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(@"telemetry\.example\.net", null, null)]
    [InlineData("telemetry", "CONNECT", null)]
    [InlineData("telemetry", null, "^/$")]
    public async Task MatchConnectionRulesAsync_MatchingDropRule_ReturnsIt(string host, string? method, string? path)
    {
        var rule = ConnRule("block telemetry", host, method, path);
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), Substitute.For<IAlertingService>());

        var match = await MatchTelemetry(svc);

        Assert.Same(rule, match.Drop);
        Assert.Empty(match.Delays);
    }

    [Fact]
    public async Task MatchConnectionRulesAsync_IgnoresRulesThatCannotMatchAConnection()
    {
        var svc = MakeService(MakeRuleCache(
                ConnRule("other host", host: "example.com"),
                ConnRule("GET only", host: "telemetry", method: "^GET$"),
                ConnRule("api path", host: "telemetry", path: "^/api"),
                ConnRule("response phase", host: "telemetry", phase: ManipulationPhase.Response),
                ConnRule("not a drop", host: "telemetry", action: ManipulationRuleAction.ModifyHeader),
                ConnRule("zero delay", host: "telemetry", action: ManipulationRuleAction.Delay, delayMs: 0),
                new ManipulationRule { Name = "chained", HostPattern = "telemetry", Action = ManipulationRuleAction.Drop, ParentRuleId = Guid.NewGuid() }),
            MakeBreakpointRepo(), Substitute.For<IAlertingService>());

        var match = await MatchTelemetry(svc);

        Assert.True(match.IsEmpty);
    }

    [Fact]
    public async Task MatchConnectionRulesAsync_DelaysBeforeDrop_AreReturnedAndSummed()
    {
        var delay1 = ConnRule("tarpit", host: "telemetry", action: ManipulationRuleAction.Delay, delayMs: 30_000);
        var delay2 = ConnRule("extra", host: "telemetry", action: ManipulationRuleAction.Delay, delayMs: 5_000);
        var drop = ConnRule("block", host: "telemetry");
        var after = ConnRule("after drop", host: "telemetry", action: ManipulationRuleAction.Delay, delayMs: 1_000);
        var svc = MakeService(MakeRuleCache(delay1, delay2, drop, after), MakeBreakpointRepo(), Substitute.For<IAlertingService>());

        var match = await MatchTelemetry(svc);

        Assert.Equal([delay1, delay2], match.Delays);
        Assert.Same(drop, match.Drop);
        Assert.Equal(35_000, match.TotalDelayMs);
    }

    [Fact]
    public async Task MatchConnectionRulesAsync_DelayOnly_HasNoDrop()
    {
        var delay = ConnRule("tarpit", host: "telemetry", action: ManipulationRuleAction.Delay, delayMs: 30_000);
        var svc = MakeService(MakeRuleCache(delay), MakeBreakpointRepo(), Substitute.For<IAlertingService>());

        var match = await MatchTelemetry(svc);

        Assert.Null(match.Drop);
        Assert.Equal(30_000, match.TotalDelayMs);
    }

    [Fact]
    public async Task MatchConnectionRulesAsync_AlertOnMatch_SendsAlert()
    {
        var rule = ConnRule("block telemetry", host: "telemetry");
        rule.AlertOnMatch = true;
        var alerting = Substitute.For<IAlertingService>();
        var svc = MakeService(MakeRuleCache(rule), MakeBreakpointRepo(), alerting);

        await MatchTelemetry(svc);

        await alerting.Received(1).SendAlertAsync(
            Arg.Is<string>(t => t.Contains("block telemetry")), Arg.Any<string>(), Arg.Any<AlertSeverity>(), Arg.Any<CancellationToken>());
    }
}
