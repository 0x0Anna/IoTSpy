using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Proxy.Tests;

/// <summary>
/// Unit tests for ProxyService state management.
/// These tests verify behaviour that doesn't require real TCP listeners or network interfaces.
/// </summary>
public class ProxyServiceTests
{
    /// <summary>
    /// Builds a ProxyService with mocked repositories and a no-op scope factory.
    /// The concrete proxy server fields are not exercised here; only state queries are tested.
    /// </summary>
    private static (ProxyService service, IProxySettingsRepository settingsRepo) CreateService(
        ProxySettings? settings = null)
    {
        settings ??= new ProxySettings { ProxyPort = 8888 };

        var settingsRepo = Substitute.For<IProxySettingsRepository>();
        settingsRepo.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);
        settingsRepo.SaveAsync(Arg.Any<ProxySettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(settings));

        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // We are testing ProxyService state; the concrete proxy server constructor
        // dependencies are only called when StartAsync / StopAsync are invoked,
        // which is out of scope for these unit tests.
        var svc = new ProxyService(
            null!, null!, null!, null!,
            scopeFactory, new ConfigurationBuilder().Build(), NullLogger<ProxyService>.Instance);

        return (svc, settingsRepo);
    }

    [Fact]
    public void GetSettings_BeforeStart_ReturnsEmptyDefaultSettings()
    {
        var (svc, _) = CreateService();
        var settings = svc.GetSettings();
        Assert.NotNull(settings);
    }

    [Fact]
    public void Port_BeforeStart_ReturnsDefaultPort()
    {
        // Port is backed by _settings which is null before StartAsync.
        // The property returns the fallback of 8888.
        var (svc, _) = CreateService(new ProxySettings { ProxyPort = 9000 });
        Assert.Equal(8888, svc.Port);
    }
}
