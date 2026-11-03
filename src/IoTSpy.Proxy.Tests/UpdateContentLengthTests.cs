using IoTSpy.Proxy.Interception;
using Xunit;

namespace IoTSpy.Proxy.Tests;

/// <summary>
/// Regression: a response-phase rule (e.g. status override 200 + header) on an upstream
/// response with an empty body used to strip Content-Length entirely, leaving a
/// close-delimited 200 on a keep-alive connection — the client hung waiting for a body.
/// </summary>
public class UpdateContentLengthTests
{
    public static TheoryData<Func<string, int, bool, string>> Impls => new()
    {
        { (h, n, z) => ExplicitProxyServer.UpdateContentLength(h, n, z) },
        { (h, n, z) => TransparentProxyServer.UpdateContentLength(h, n, z) },
    };

    [Theory, MemberData(nameof(Impls))]
    public void EmptyBody_EmitZero_WritesContentLengthZero(Func<string, int, bool, string> update)
    {
        var result = update("Content-Length: 0\r\nAllow: GET, HEAD", 0, true);

        Assert.Contains("Content-Length: 0", result);
        Assert.Single(result.Split("\r\n"), l => l.StartsWith("Content-Length:"));
    }

    [Theory, MemberData(nameof(Impls))]
    public void EmptyBody_Default_OmitsContentLength(Func<string, int, bool, string> update)
    {
        // Request path: no Content-Length already means "no body".
        var result = update("Content-Length: 5\r\nHost: example.com", 0, false);

        Assert.DoesNotContain("Content-Length", result);
        Assert.Contains("Host: example.com", result);
    }

    [Theory, MemberData(nameof(Impls))]
    public void NonEmptyBody_ReplacesExistingLength(Func<string, int, bool, string> update)
    {
        var result = update("Content-Length: 3\r\nX-A: 1", 42, false);

        Assert.Contains("Content-Length: 42", result);
        Assert.DoesNotContain("Content-Length: 3", result);
    }
}
