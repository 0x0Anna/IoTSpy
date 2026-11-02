using System.Net;
using System.Net.Sockets;
using IoTSpy.Proxy.Interception;
using Xunit;

namespace IoTSpy.Proxy.Tests;

public class OriginalDestinationTests
{
    [Fact]
    public void TryParseSockaddrIn_ReadsPortAndAddressInNetworkOrder()
    {
        // AF_INET (2, host order on Linux), port 443, 203.0.113.7, 8 zero bytes
        byte[] sockaddr = [0x02, 0x00, 0x01, 0xBB, 203, 0, 113, 7, 0, 0, 0, 0, 0, 0, 0, 0];

        var ok = OriginalDestination.TryParseSockaddrIn(sockaddr, out var ip, out var port, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ip);
        Assert.Equal(443, port);
    }

    [Fact]
    public void TryParseSockaddrIn_HighPort_IsNotSignExtended()
    {
        // 0xFFFF must parse as 65535, not -1
        byte[] sockaddr = [0x02, 0x00, 0xFF, 0xFF, 10, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(OriginalDestination.TryParseSockaddrIn(sockaddr, out _, out var port, out _));
        Assert.Equal(65535, port);
    }

    [Fact]
    public void TryParseSockaddrIn_TooShort_Fails()
    {
        var ok = OriginalDestination.TryParseSockaddrIn(new byte[8], out var ip, out var port, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(IPAddress.None, ip);
        Assert.Equal(0, port);
    }

    [Fact]
    public async Task TryGet_OnConnectionThatWasNotRedirected_DoesNotThrow()
    {
        // Without a REDIRECT rule the result depends on the host: with conntrack loaded the kernel
        // reports the connection's own destination, otherwise getsockopt fails. Either way it must
        // return normally instead of throwing, and a failure must say why.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await connect;

            var ok = OriginalDestination.TryGet(accepted.Client, out var ip, out var origPort, out var error);

            if (ok)
            {
                Assert.Equal(IPAddress.Loopback, ip);
                Assert.Equal(port, origPort);
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(error));
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
