using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Recovers the pre-NAT destination of a connection redirected by iptables/nftables
/// <c>REDIRECT</c> (Linux netfilter <c>SO_ORIGINAL_DST</c>).
/// </summary>
public static class OriginalDestination
{
    // Linux netfilter constants (include/uapi/linux/netfilter_ipv4.h, linux/in.h).
    private const int SolIp = 0;            // SOL_IP
    private const int SoOriginalDst = 80;   // SO_ORIGINAL_DST

    // struct sockaddr_in: family(2) + port(2, network order) + addr(4) + zero(8)
    private const int SockaddrInLength = 16;

    /// <summary>
    /// Reads the original destination of an accepted IPv4 socket. Returns false (and sets
    /// <paramref name="error"/>) when it cannot be determined, for example because the
    /// connection was not redirected or the platform is not Linux.
    /// </summary>
    public static bool TryGet(Socket socket, out IPAddress ip, out int port, out string? error)
    {
        ip = IPAddress.None;
        port = 0;
        error = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            error = "SO_ORIGINAL_DST is only available on Linux";
            return false;
        }

        try
        {
            // GetSocketOption(SocketOptionLevel, SocketOptionName, byte[]) only accepts option
            // names that .NET maps to a platform constant, and SO_ORIGINAL_DST is not one of them
            // (it throws SocketException "Operation not supported"). The raw API passes the
            // level and option number straight through to getsockopt.
            var buffer = new byte[SockaddrInLength];
            var length = socket.GetRawSocketOption(SolIp, SoOriginalDst, buffer);
            return TryParseSockaddrIn(buffer.AsSpan(0, length), out ip, out port, out error);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ObjectDisposedException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Parses a Linux <c>struct sockaddr_in</c> as returned by SO_ORIGINAL_DST.</summary>
    public static bool TryParseSockaddrIn(ReadOnlySpan<byte> sockaddr, out IPAddress ip, out int port, out string? error)
    {
        ip = IPAddress.None;
        port = 0;
        error = null;

        if (sockaddr.Length < SockaddrInLength)
        {
            error = $"sockaddr_in too short ({sockaddr.Length} bytes)";
            return false;
        }

        // Port is bytes 2-3 and the address bytes 4-7, both in network byte order.
        port = (sockaddr[2] << 8) | sockaddr[3];
        ip = new IPAddress(sockaddr.Slice(4, 4));
        return true;
    }
}
