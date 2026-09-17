using System.Net;
using IoTSpy.Core.Models;

namespace IoTSpy.Scanner;

public static class CidrHelper
{
    /// <summary>
    /// Returns true if <paramref name="cidr"/> is a parseable CIDR block or bare IP address.
    /// </summary>
    public static bool IsValidCidr(string cidr) => TryParseCidr(cidr, out _, out _);

    /// <summary>
    /// The scan-scope consent gate shared by <c>ScannerController.StartScan</c> (single-device,
    /// interactive scans) and <c>ScheduledScanService</c> (unattended, possibly multi-device scans).
    /// When no active scopes are configured, every device is considered in scope (unrestricted mode).
    /// Otherwise the device's IP must fall within at least one active scope's CIDR.
    /// </summary>
    public static bool IsInScope(IReadOnlyCollection<ScanScope> activeScopes, string ipAddress) =>
        activeScopes.Count == 0 || activeScopes.Any(s => Contains(s.Cidr, ipAddress));

    /// <summary>
    /// Returns true if <paramref name="ipString"/> falls within the <paramref name="cidr"/> block.
    /// Supports both IPv4 and IPv6. Returns false for any unparseable input.
    /// </summary>
    public static bool Contains(string cidr, string ipString)
    {
        if (!TryParseCidr(cidr, out var networkAddress, out var prefixLength))
            return false;

        if (!IPAddress.TryParse(ipString, out var candidate))
            return false;

        if (networkAddress.AddressFamily != candidate.AddressFamily)
            return false;

        var networkBytes = networkAddress.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != candidateBytes[i])
                return false;
        }

        if (remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            if ((networkBytes[fullBytes] & mask) != (candidateBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    private static bool TryParseCidr(string cidr, out IPAddress address, out int prefixLength)
    {
        address = IPAddress.None;
        prefixLength = 0;

        var slash = cidr.IndexOf('/');
        if (slash < 0)
        {
            // bare IP = /32 or /128
            if (!IPAddress.TryParse(cidr, out var bare)) return false;
            address = bare;
            prefixLength = bare.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return true;
        }

        if (!IPAddress.TryParse(cidr[..slash], out var net)) return false;
        if (!int.TryParse(cidr[(slash + 1)..], out prefixLength)) return false;

        int maxPrefix = net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixLength < 0 || prefixLength > maxPrefix) return false;

        address = net;
        return true;
    }
}
