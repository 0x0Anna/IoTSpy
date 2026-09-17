using System.Text;
using IoTSpy.Api.Controllers;
using IoTSpy.Api.Services;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Interception;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class FirewallControllerTests
{
    private static FirewallController CreateController(
        IProxySettingsRepository? proxySettingsRepo = null,
        IWindowsFirewallHelper? firewallHelper = null,
        IPlatformInfo? platformInfo = null,
        IAuditRepository? audit = null)
    {
        var repo = proxySettingsRepo ?? Substitute.For<IProxySettingsRepository>();
        repo.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new ProxySettings { ProxyPort = 8888, TransparentProxyPort = 9999 });

        var controller = new FirewallController(
            repo,
            firewallHelper ?? Substitute.For<IWindowsFirewallHelper>(),
            platformInfo ?? Substitute.For<IPlatformInfo>(),
            audit ?? Substitute.For<IAuditRepository>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task GetScript_Windows_ReturnsPs1WithExpectedPorts()
    {
        var controller = CreateController();

        var result = await controller.GetScript("windows", TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("iotspy-configure-firewall.ps1", result.FileDownloadName);
        var content = Encoding.UTF8.GetString(result.FileContents);
        Assert.Contains("localport=5000", content);
        Assert.Contains("localport=5001", content);
        Assert.Contains("localport=8888", content);
        Assert.Contains("localport=9999", content);
        Assert.Contains("localport=1883", content);
        Assert.Contains("localport=5683", content);
        Assert.Contains("protocol=UDP", content);
        Assert.Contains("Run this script from an elevated", content);
    }

    [Theory]
    [InlineData("mac")]
    [InlineData("linux")]
    public async Task GetScript_UnsupportedPlatform_Returns501(string platform)
    {
        var controller = CreateController();

        var result = await controller.GetScript(platform, TestContext.Current.CancellationToken) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(501, result.StatusCode);
    }

    [Fact]
    public async Task Configure_NonWindows_Returns501()
    {
        var platformInfo = Substitute.For<IPlatformInfo>();
        platformInfo.IsWindows.Returns(false);
        var controller = CreateController(platformInfo: platformInfo);

        var result = await controller.Configure(null, TestContext.Current.CancellationToken) as ObjectResult;

        Assert.NotNull(result);
        Assert.Equal(501, result.StatusCode);
    }

    [Fact]
    public async Task Configure_Windows_ReturnsPerRuleResultsAndAudits()
    {
        var platformInfo = Substitute.For<IPlatformInfo>();
        platformInfo.IsWindows.Returns(true);

        var firewallHelper = Substitute.For<IWindowsFirewallHelper>();
        firewallHelper.ConfigureRulesAsync(Arg.Any<IEnumerable<FirewallRule>>())
            .Returns(ci =>
            {
                var rules = ((IEnumerable<FirewallRule>)ci[0]).ToList();
                return (IReadOnlyList<FirewallRuleResult>)rules
                    .Select(r => new FirewallRuleResult(r.Port, r.Protocol, r.Label, r.Port != 5001, r.Port == 5001 ? "Access denied" : "OK"))
                    .ToList();
            });

        var audit = Substitute.For<IAuditRepository>();
        var controller = CreateController(platformInfo: platformInfo, firewallHelper: firewallHelper, audit: audit);

        var result = await controller.Configure(null, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var response = Assert.IsType<FirewallController.ConfigureFirewallResponse>(result.Value);
        Assert.False(response.OverallSuccess);
        Assert.Contains(response.Results, r => r.Port == 5001 && !r.Success);
        Assert.Contains(response.Results, r => r.Port == 5000 && r.Success);

        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "FirewallRulesConfigured"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Configure_Windows_AllSucceed_OverallSuccessTrue()
    {
        var platformInfo = Substitute.For<IPlatformInfo>();
        platformInfo.IsWindows.Returns(true);

        var firewallHelper = Substitute.For<IWindowsFirewallHelper>();
        firewallHelper.ConfigureRulesAsync(Arg.Any<IEnumerable<FirewallRule>>())
            .Returns(ci =>
            {
                var rules = ((IEnumerable<FirewallRule>)ci[0]).ToList();
                return (IReadOnlyList<FirewallRuleResult>)rules
                    .Select(r => new FirewallRuleResult(r.Port, r.Protocol, r.Label, true, "OK"))
                    .ToList();
            });

        var controller = CreateController(platformInfo: platformInfo, firewallHelper: firewallHelper);

        var result = await controller.Configure(null, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var response = Assert.IsType<FirewallController.ConfigureFirewallResponse>(result.Value);
        Assert.True(response.OverallSuccess);
    }
}
