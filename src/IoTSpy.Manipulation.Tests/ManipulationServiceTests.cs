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
}
