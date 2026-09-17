using System.Net;
using IoTSpy.Core.Models;
using IoTSpy.Manipulation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace IoTSpy.Manipulation.Tests;

public class ReplayServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private static (ReplayService service, CapturingHandler handler) MakeService()
    {
        var handler = new CapturingHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var service = new ReplayService(factory, NullLogger<ReplayService>.Instance);
        return (service, handler);
    }

    private static ReplaySession MakeSession() => new()
    {
        RequestMethod = "GET",
        RequestScheme = "https",
        RequestHost = "original.example.com",
        RequestPort = 443,
        RequestPath = "/api/original",
        RequestQuery = "",
    };

    [Fact]
    public async Task ExecuteReplayAsync_DefaultSession_HitsOriginalHost()
    {
        var (service, handler) = MakeService();
        var session = MakeSession();

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal("original.example.com", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal("/api/original", handler.LastRequest.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task ExecuteReplayAsync_HostOverride_RedirectsToOverriddenHost()
    {
        var (service, handler) = MakeService();
        var session = MakeSession();
        session.RequestHost = "override.example.com";

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal("override.example.com", handler.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task ExecuteReplayAsync_PortOverride_ChangesRequestUriPort()
    {
        var (service, handler) = MakeService();
        var session = MakeSession();
        session.RequestPort = 8443;

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(8443, handler.LastRequest!.RequestUri!.Port);
    }

    [Fact]
    public async Task ExecuteReplayAsync_PathAndQueryOverride_ChangesRequestUri()
    {
        var (service, handler) = MakeService();
        var session = MakeSession();
        session.RequestPath = "/override/path";
        session.RequestQuery = "?a=1&b=2";

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal("/override/path", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?a=1&b=2", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task ExecuteReplayAsync_DefaultHttpsPort_IsOmittedFromAuthority()
    {
        var (service, handler) = MakeService();
        var session = MakeSession();
        session.RequestPort = 443;

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(":443", handler.LastRequest!.RequestUri!.Authority);
    }

    [Fact]
    public async Task ExecuteReplayAsync_LiteralHostHeader_IsStrippedFromOutgoingRequest()
    {
        // Intentional: a raw "Host:" header would conflict with the connection target
        // HttpClient already sets from the request URI, so ReplayService drops it rather
        // than let it silently fight the real target.
        var (service, handler) = MakeService();
        var session = MakeSession();
        session.RequestHeaders = "Host: spoofed.example.com\r\nX-Custom: keep-me\r\n";

        await service.ExecuteReplayAsync(session, TestContext.Current.CancellationToken);

        Assert.False(handler.LastRequest!.Headers.TryGetValues("Host", out _));
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Custom", out var values));
        Assert.Equal("keep-me", values!.Single());
    }
}
