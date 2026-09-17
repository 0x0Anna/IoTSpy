namespace IoTSpy.Core.Utilities;

/// <summary>
/// Parses the raw <c>Name: Value\r\n...</c> HTTP header text stored in
/// <see cref="Models.CapturedRequest.RequestHeaders"/> / <see cref="Models.CapturedRequest.ResponseHeaders"/>.
/// Despite those properties' XML docs, the proxy pipeline writes raw header lines, not JSON —
/// callers must not assume a JSON shape.
/// </summary>
public static class HttpHeaderParser
{
    public static List<(string Name, string Value)> ParseHeaderLines(string? raw)
    {
        var result = new List<(string Name, string Value)>();
        if (string.IsNullOrEmpty(raw)) return result;

        foreach (var line in raw.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            var name = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            if (name.Length > 0)
                result.Add((name, value));
        }

        return result;
    }

    public static string? FindHeaderValue(string? raw, string headerName)
    {
        foreach (var (name, value) in ParseHeaderLines(raw))
        {
            if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }
}
