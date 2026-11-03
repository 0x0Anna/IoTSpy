using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IoTSpy.Proxy.Tls;

/// <summary>
/// Parses a TLS ClientHello from raw bytes to extract SNI hostname, cipher suites,
/// extensions, and compute a JA3 fingerprint.
/// </summary>
public static class TlsClientHelloParser
{
    // GREASE values defined by RFC 8701 — excluded from JA3 computation
    private static readonly HashSet<ushort> GreaseValues =
    [
        0x0A0A, 0x1A1A, 0x2A2A, 0x3A3A, 0x4A4A, 0x5A5A, 0x6A6A, 0x7A7A,
        0x8A8A, 0x9A9A, 0xAAAA, 0xBABA, 0xCACA, 0xDADA, 0xEAEA, 0xFAFA
    ];

    /// <summary>
    /// Attempts to parse a TLS ClientHello from a buffer that starts with a TLS record header.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out ClientHelloInfo info)
    {
        info = default!;

        // TLS record: ContentType(1) + Version(2) + Length(2) = 5 bytes minimum
        if (data.Length < 5) return false;
        if (data[0] != 0x16) return false; // ContentType.Handshake

        var recordLength = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        if (data.Length < 5 + recordLength) return false;

        var handshake = data[5..];

        // Handshake header: Type(1) + Length(3) = 4 bytes
        if (handshake.Length < 4) return false;
        if (handshake[0] != 0x01) return false; // HandshakeType.ClientHello

        var hsLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
        if (handshake.Length < 4 + hsLength) return false;

        var hello = handshake[4..];
        var pos = 0;

        // ClientVersion (2 bytes)
        if (pos + 2 > hello.Length) return false;
        var clientVersion = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
        pos += 2;

        // Random (32 bytes)
        pos += 32;
        if (pos > hello.Length) return false;

        // SessionID (variable length)
        if (pos + 1 > hello.Length) return false;
        var sessionIdLen = hello[pos++];
        pos += sessionIdLen;
        if (pos > hello.Length) return false;

        // Cipher suites (2-byte count + 2 bytes each)
        if (pos + 2 > hello.Length) return false;
        var cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
        pos += 2;
        if (pos + cipherSuitesLen > hello.Length) return false;

        var cipherSuites = new List<ushort>();
        var cipherEnd = pos + cipherSuitesLen;
        while (pos + 2 <= cipherEnd)
        {
            var cs = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
            pos += 2;
            cipherSuites.Add(cs);
        }

        // Compression methods (1-byte count + 1 byte each)
        if (pos + 1 > hello.Length) return false;
        var compLen = hello[pos++];
        pos += compLen;
        if (pos > hello.Length) return false;

        // Extensions
        var extensions = new List<ushort>();
        var ellipticCurves = new List<ushort>();
        var ecPointFormats = new List<byte>();
        string sniHostname = string.Empty;
        var alpnProtocols = new List<string>();

        if (pos + 2 <= hello.Length)
        {
            var extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
            pos += 2;
            var extEnd = pos + extTotalLen;
            if (extEnd > hello.Length) extEnd = hello.Length;

            while (pos + 4 <= extEnd)
            {
                var extType = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
                var extLen = BinaryPrimitives.ReadUInt16BigEndian(hello[(pos + 2)..]);
                pos += 4;

                extensions.Add(extType);

                var extData = hello.Slice(pos, Math.Min(extLen, extEnd - pos));

                switch (extType)
                {
                    case 0x0000: // SNI
                        sniHostname = ParseSni(extData);
                        break;
                    case 0x000A: // supported_groups (elliptic_curves)
                        ellipticCurves = ParseUInt16List(extData);
                        break;
                    case 0x000B: // ec_point_formats
                        ecPointFormats = ParseByteList(extData);
                        break;
                    case 0x0010: // application_layer_protocol_negotiation (RFC 7301)
                        alpnProtocols = ParseAlpn(extData);
                        break;
                }

                pos += extLen;
                if (pos > extEnd) break; // Extension overran declared bounds
            }
        }

        // Compute JA3: TLSVersion,Ciphers,Extensions,EllipticCurves,EllipticCurvePointFormats
        var ja3Ciphers = string.Join("-", cipherSuites.Where(c => !GreaseValues.Contains(c)));
        var ja3Extensions = string.Join("-", extensions.Where(e => !GreaseValues.Contains(e)));
        var ja3Curves = string.Join("-", ellipticCurves.Where(c => !GreaseValues.Contains(c)));
        var ja3PointFormats = string.Join("-", ecPointFormats);

        var ja3Raw = $"{clientVersion},{ja3Ciphers},{ja3Extensions},{ja3Curves},{ja3PointFormats}";
        var ja3Hash = ComputeMd5(ja3Raw);

        info = new ClientHelloInfo
        {
            SniHostname = sniHostname,
            TlsVersion = clientVersion,
            CipherSuites = cipherSuites,
            Extensions = extensions,
            EllipticCurves = ellipticCurves,
            EcPointFormats = ecPointFormats,
            Ja3Raw = ja3Raw,
            Ja3Hash = ja3Hash,
            AlpnProtocols = alpnProtocols
        };

        return true;
    }

    // ProtocolNameList: uint16 total length, then (uint8 length, bytes) entries.
    private static List<string> ParseAlpn(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();
        if (data.Length < 2) return result;
        var listLen = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data), data.Length - 2);
        var list = data.Slice(2, listLen);
        var pos = 0;
        while (pos < list.Length)
        {
            var len = list[pos++];
            if (len == 0 || pos + len > list.Length) break;
            result.Add(Encoding.ASCII.GetString(list.Slice(pos, len)));
            pos += len;
        }
        return result;
    }

    /// <summary>
    /// Determines how many bytes constitute the complete first TLS record in the buffer,
    /// so the caller knows how much to buffer before parsing.
    /// Returns 0 if insufficient data to determine length.
    /// </summary>
    public static int GetRecordLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return 0;
        if (data[0] != 0x16) return 0;
        return 5 + BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
    }

    private static string ParseSni(ReadOnlySpan<byte> data)
    {
        // SNI extension: ListLength(2) + Type(1) + NameLength(2) + Name
        if (data.Length < 5) return string.Empty;
        var listLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (data.Length < 2 + listLen) return string.Empty;

        var pos = 2;
        while (pos + 3 < data.Length)
        {
            var nameType = data[pos++];
            var nameLen = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;
            if (nameType == 0 && pos + nameLen <= data.Length) // host_name
                return Encoding.ASCII.GetString(data.Slice(pos, nameLen));
            pos += nameLen;
        }
        return string.Empty;
    }

    private static List<ushort> ParseUInt16List(ReadOnlySpan<byte> data)
    {
        var result = new List<ushort>();
        if (data.Length < 2) return result;
        var listLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        var pos = 2;
        var end = 2 + listLen;
        while (pos + 2 <= end && pos + 2 <= data.Length)
        {
            result.Add(BinaryPrimitives.ReadUInt16BigEndian(data[pos..]));
            pos += 2;
        }
        return result;
    }

    private static List<byte> ParseByteList(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>();
        if (data.Length < 1) return result;
        var listLen = data[0];
        for (var i = 1; i <= listLen && i < data.Length; i++)
            result.Add(data[i]);
        return result;
    }

    private static string ComputeMd5(string input)
    {
        var hash = MD5.HashData(Encoding.ASCII.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}

public record ClientHelloInfo
{
    public string SniHostname { get; init; } = string.Empty;
    public ushort TlsVersion { get; init; }
    public List<ushort> CipherSuites { get; init; } = [];
    public List<ushort> Extensions { get; init; } = [];
    public List<ushort> EllipticCurves { get; init; } = [];
    public List<byte> EcPointFormats { get; init; } = [];
    public string Ja3Raw { get; init; } = string.Empty;
    public string Ja3Hash { get; init; } = string.Empty;
    /// <summary>ALPN protocol names offered by the client, in preference order. Empty when the
    /// client sent no ALPN extension.</summary>
    public List<string> AlpnProtocols { get; init; } = [];
}
