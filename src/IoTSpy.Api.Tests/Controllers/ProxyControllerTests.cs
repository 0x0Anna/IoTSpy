using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class ProxyControllerTests
{
    private static IProxyService CreateProxyService(bool isRunning = false, int port = 8888)
    {
        var svc = Substitute.For<IProxyService>();
        svc.IsRunning.Returns(isRunning);
        svc.Port.Returns(port);
        svc.GetSettings().Returns(new ProxySettings { ProxyPort = port });
        return svc;
    }

    [Fact]
    public void Status_ReturnsCurrentState()
    {
        var proxy = CreateProxyService(isRunning: true, port: 8888);
        var controller = new ProxyController(proxy);

        var result = controller.Status() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("true", json);
        Assert.Contains("8888", json);
    }

    [Fact]
    public void Status_WhenNotRunning_ReturnsIsRunningFalse()
    {
        var proxy = CreateProxyService(isRunning: false);
        var controller = new ProxyController(proxy);

        var result = controller.Status() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("false", json);
    }

    [Fact]
    public async Task Start_CallsProxyStartAndReturnsOk()
    {
        var proxy = CreateProxyService();
        proxy.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        var result = await controller.Start() as OkObjectResult;

        Assert.NotNull(result);
        await proxy.Received(1).StartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_CallsProxyStopAndReturnsOk()
    {
        var proxy = CreateProxyService(isRunning: true);
        proxy.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        var result = await controller.Stop() as OkObjectResult;

        Assert.NotNull(result);
        await proxy.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_CallsUpdateAndReturnsSettings()
    {
        var proxy = CreateProxyService();
        proxy.UpdateSettingsAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        var dto = new UpdateProxySettingsDto(ProxyPort: 9999);
        var result = await controller.UpdateSettings(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        await proxy.Received(1).UpdateSettingsAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_SetsTlsPassthroughOptions()
    {
        var proxy = CreateProxyService();
        ProxySettings? saved = null;
        proxy.UpdateSettingsAsync(Arg.Do<ProxySettings>(s => saved = s), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        var dto = new UpdateProxySettingsDto(TlsPassthroughHosts: "  *.apple.com\ngateway.icloud.com  ", AutoPassthroughOnTlsFailure: true);
        await controller.UpdateSettings(dto, TestContext.Current.CancellationToken);

        Assert.NotNull(saved);
        Assert.Equal("*.apple.com\ngateway.icloud.com", saved!.TlsPassthroughHosts);
        Assert.True(saved.AutoPassthroughOnTlsFailure);
    }

    [Fact]
    public async Task UpdateSettings_TooLongPassthroughList_Returns400()
    {
        var proxy = CreateProxyService();
        var controller = new ProxyController(proxy);

        var result = await controller.UpdateSettings(
            new UpdateProxySettingsDto(TlsPassthroughHosts: new string('a', 16_385)), TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        await proxy.DidNotReceive().UpdateSettingsAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettings_IsPassive_KeepsTransportMode()
    {
        var proxy = CreateProxyService();
        proxy.GetSettings().Mode = ProxyMode.GatewayRedirect;
        ProxySettings? saved = null;
        proxy.UpdateSettingsAsync(Arg.Do<ProxySettings>(s => saved = s), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        await controller.UpdateSettings(new UpdateProxySettingsDto(IsPassive: true), TestContext.Current.CancellationToken);

        Assert.NotNull(saved);
        Assert.True(saved!.IsPassive);
        Assert.Equal(ProxyMode.GatewayRedirect, saved.Mode);
    }

    [Fact]
    public async Task UpdateSettings_IsPassiveFalse_LeavesPassive()
    {
        var proxy = CreateProxyService();
        proxy.GetSettings().Mode = ProxyMode.GatewayRedirect;
        proxy.GetSettings().IsPassive = true;
        ProxySettings? saved = null;
        proxy.UpdateSettingsAsync(Arg.Do<ProxySettings>(s => saved = s), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        await controller.UpdateSettings(new UpdateProxySettingsDto(IsPassive: false), TestContext.Current.CancellationToken);

        Assert.False(saved!.IsPassive);
        Assert.Equal(ProxyMode.GatewayRedirect, saved.Mode);
    }

    [Fact]
    public async Task UpdateSettings_OmittedIsPassive_PreservesIt()
    {
        var proxy = CreateProxyService();
        proxy.GetSettings().IsPassive = true;
        ProxySettings? saved = null;
        proxy.UpdateSettingsAsync(Arg.Do<ProxySettings>(s => saved = s), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

        await controller.UpdateSettings(new UpdateProxySettingsDto(ProxyPort: 8899), TestContext.Current.CancellationToken);

        Assert.True(saved!.IsPassive);
    }

    [Fact]
    public async Task UpdateSettings_LegacyPassiveMode_NormalizedToExplicitPlusFlag()
    {
        var proxy = CreateProxyService();
        ProxySettings? saved = null;
        proxy.UpdateSettingsAsync(Arg.Do<ProxySettings>(s => saved = s), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var controller = new ProxyController(proxy);

#pragma warning disable CS0618 // exercising the legacy value on purpose
        await controller.UpdateSettings(new UpdateProxySettingsDto(Mode: ProxyMode.Passive), TestContext.Current.CancellationToken);
#pragma warning restore CS0618

        Assert.Equal(ProxyMode.ExplicitProxy, saved!.Mode);
        Assert.True(saved.IsPassive);
    }
}
