using IoTSpy.Core.Interfaces;
using IoTSpy.Proxy.Interception;
using IoTSpy.Proxy.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Proxy.Tests;

/// <summary>
/// Unit tests verifying that the graceful shutdown logic added in Phase 8.6
/// correctly tracks active connection counts.
/// </summary>
public class GracefulShutdownTests
{
    [Fact]
    public void ExplicitProxyServer_NotRunning_Initially()
    {
        // Arrange / Act
        var server = CreateExplicitProxy();

        // Assert — should not be running before StartAsync
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task ExplicitProxyServer_StopAsync_CompletesWhenNoActiveConnections()
    {
        var server = CreateExplicitProxy();
        // StopAsync when not running (no active connections) should complete immediately
        await server.StopAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void TransparentProxyServer_NotRunning_Initially()
    {
        var server = CreateTransparentProxy();
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task TransparentProxyServer_StopAsync_CompletesWhenNoActiveConnections()
    {
        var server = CreateTransparentProxy();
        await server.StopAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(server.IsRunning);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ExplicitProxyServer CreateExplicitProxy()
    {
        return new ExplicitProxyServer(
            Substitute.For<ICertificateAuthority>(),
            Substitute.For<ICapturePublisher>(),
            Substitute.For<IManipulationService>(),
            Substitute.For<IOpenRtbService>(),
            Substitute.For<IAnomalyDetector>(),
            Substitute.For<IAnomalyAlertPublisher>(),
            new SslStripService(NullLogger<SslStripService>.Instance),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<Polly.Registry.ResiliencePipelineProvider<string>>(),
            Substitute.For<IPerHostConnectPipelineCache>(),
            Substitute.For<IPassiveProxyBuffer>(),
            Substitute.For<ICaptureBatchWriter>(),
            Substitute.For<IProtocolMessageWriter>(),
            NullLogger<ExplicitProxyServer>.Instance);
    }

    private static TransparentProxyServer CreateTransparentProxy()
    {
        return new TransparentProxyServer(
            Substitute.For<ICertificateAuthority>(),
            Substitute.For<ICapturePublisher>(),
            Substitute.For<IManipulationService>(),
            Substitute.For<IOpenRtbService>(),
            Substitute.For<IAnomalyDetector>(),
            Substitute.For<IAnomalyAlertPublisher>(),
            new SslStripService(NullLogger<SslStripService>.Instance),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<Polly.Registry.ResiliencePipelineProvider<string>>(),
            Substitute.For<IPerHostConnectPipelineCache>(),
            Substitute.For<IProtocolMessageWriter>(),
            NullLogger<TransparentProxyServer>.Instance);
    }
}
