using IoTSpy.Core.Utilities;
using Xunit;

namespace IoTSpy.Core.Tests;

public class HttpHeaderParserTests
{
    [Fact]
    public void ParseHeaderLines_MultipleHeaders_ReturnsAllPairs()
    {
        var raw = "Host: example.com\r\nContent-Type: application/json\r\nX-Custom: value1\r\n";

        var result = HttpHeaderParser.ParseHeaderLines(raw);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, h => h.Name == "Host" && h.Value == "example.com");
        Assert.Contains(result, h => h.Name == "Content-Type" && h.Value == "application/json");
        Assert.Contains(result, h => h.Name == "X-Custom" && h.Value == "value1");
    }

    [Fact]
    public void ParseHeaderLines_NullOrEmpty_ReturnsEmptyList()
    {
        Assert.Empty(HttpHeaderParser.ParseHeaderLines(null));
        Assert.Empty(HttpHeaderParser.ParseHeaderLines(""));
    }

    [Fact]
    public void ParseHeaderLines_ValueContainsColon_KeepsFullValue()
    {
        var raw = "Location: https://example.com:8443/path\r\n";

        var result = HttpHeaderParser.ParseHeaderLines(raw);

        Assert.Single(result);
        Assert.Equal("Location", result[0].Name);
        Assert.Equal("https://example.com:8443/path", result[0].Value);
    }

    [Fact]
    public void ParseHeaderLines_TrailingBlankLines_AreSkipped()
    {
        var raw = "Host: example.com\r\n\r\n\r\n";

        var result = HttpHeaderParser.ParseHeaderLines(raw);

        Assert.Single(result);
    }

    [Fact]
    public void FindHeaderValue_IsCaseInsensitive()
    {
        var raw = "content-type: application/json\r\n";

        var value = HttpHeaderParser.FindHeaderValue(raw, "Content-Type");

        Assert.Equal("application/json", value);
    }

    [Fact]
    public void FindHeaderValue_NotFound_ReturnsNull()
    {
        var raw = "Host: example.com\r\n";

        Assert.Null(HttpHeaderParser.FindHeaderValue(raw, "Content-Type"));
    }
}
