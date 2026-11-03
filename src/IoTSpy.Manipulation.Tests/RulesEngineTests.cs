using Xunit;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;
using IoTSpy.Manipulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTSpy.Manipulation.Tests;

public class RulesEngineTests
{
    private static RulesEngine CreateEngine() => new(NullLogger<RulesEngine>.Instance);

    private static HttpMessage MakeRequest(string host = "example.com", string path = "/api/test", string method = "GET") =>
        new()
        {
            Host = host,
            Path = path,
            Method = method,
            RequestHeaders = "X-Custom: value\r\nContent-Type: application/json",
            RequestBody = string.Empty
        };

    private static HttpMessage MakeResponse(int statusCode = 200, string body = "") =>
        new()
        {
            Host = "example.com",
            Path = "/api",
            Method = "GET",
            StatusCode = statusCode,
            StatusLine = $"HTTP/1.1 {statusCode} OK",
            ResponseHeaders = "Content-Type: application/json",
            ResponseBody = body
        };

    // ── Matching ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_NoRules_ReturnsFalse()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_RulePhaseDoesNotMatch_SkipsRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, // won't apply to request
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_HostPatternNoMatch_SkipsRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest(host: "example.com");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            HostPattern = "^other\\.com$",
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_HostPatternMatch_AppliesRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest(host: "example.com");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            HostPattern = "example\\.com",
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_PathPatternNoMatch_SkipsRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest(path: "/api/test");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            PathPattern = "^/admin",
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_MethodPatternNoMatch_SkipsRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest(method: "GET");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            MethodPattern = "^POST$",
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    [Fact]
    public async Task ApplyRulesAsync_AllPatternsMatch_AppliesRule()
    {
        var engine = CreateEngine();
        var msg = MakeRequest(host: "api.example.com", path: "/v1/users", method: "POST");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            HostPattern = "api\\.example\\.com",
            PathPattern = "/v1/",
            MethodPattern = "POST",
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
    }

    // ── ModifyHeader action ──────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_ModifyHeader_AddsMissingHeader()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestHeaders = "Content-Type: application/json";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.ModifyHeader,
            HeaderName = "X-Injected",
            HeaderValue = "test-value"
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Contains("X-Injected: test-value", msg.RequestHeaders);
    }

    [Fact]
    public async Task ApplyRulesAsync_ModifyHeader_ReplacesExistingHeader()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestHeaders = "X-Custom: old-value\r\nContent-Type: application/json";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.ModifyHeader,
            HeaderName = "X-Custom",
            HeaderValue = "new-value"
        };

        await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.Contains("X-Custom: new-value", msg.RequestHeaders);
        Assert.DoesNotContain("old-value", msg.RequestHeaders);
    }

    [Fact]
    public async Task ApplyRulesAsync_ModifyHeader_RemovesHeaderWhenValueNull()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestHeaders = "X-Remove-Me: value\r\nContent-Type: application/json";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.ModifyHeader,
            HeaderName = "X-Remove-Me",
            HeaderValue = null // null = remove
        };

        await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.DoesNotContain("X-Remove-Me", msg.RequestHeaders);
    }

    [Fact]
    public async Task ApplyRulesAsync_ModifyHeader_ResponsePhase_UpdatesResponseHeaders()
    {
        var engine = CreateEngine();
        var msg = MakeResponse();
        msg.ResponseHeaders = "Content-Type: text/html";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response,
            Action = ManipulationRuleAction.ModifyHeader,
            HeaderName = "X-Security",
            HeaderValue = "no-sniff"
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Contains("X-Security: no-sniff", msg.ResponseHeaders);
    }

    // ── ModifyBody action ────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_ModifyBody_ReplacesMatchingPattern()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestBody = "secret-password-123";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.ModifyBody,
            BodyReplace = @"password-\d+",
            BodyReplaceWith = "REDACTED"
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal("secret-REDACTED", msg.RequestBody);
    }

    [Fact]
    public async Task ApplyRulesAsync_ModifyBody_NoMatch_ReturnsFalse()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestBody = "hello world";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.ModifyBody,
            BodyReplace = "notfound",
            BodyReplaceWith = "replacement"
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
        Assert.Equal("hello world", msg.RequestBody);
    }

    [Fact]
    public async Task ApplyRulesAsync_ModifyBody_ResponsePhase_UpdatesResponseBody()
    {
        var engine = CreateEngine();
        var msg = MakeResponse(body: "{\"debug\":true}");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response,
            Action = ManipulationRuleAction.ModifyBody,
            BodyReplace = "true",
            BodyReplaceWith = "false"
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal("{\"debug\":false}", msg.ResponseBody);
    }

    // ── OverrideStatusCode action ────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_OverrideStatus_ResponsePhase_SetsStatusCode()
    {
        var engine = CreateEngine();
        var msg = MakeResponse(statusCode: 200);
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response,
            Action = ManipulationRuleAction.OverrideStatusCode,
            OverrideStatusCode = 403
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal(403, msg.StatusCode);
        Assert.Contains("403", msg.StatusLine);
    }

    [Fact]
    public async Task ApplyRulesAsync_OverrideStatus_RequestPhase_ReturnsFalse()
    {
        // OverrideStatusCode only applies to responses
        var engine = CreateEngine();
        var msg = MakeRequest();
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.OverrideStatusCode,
            OverrideStatusCode = 500
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
    }

    // ── Drop action ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_Drop_RequestPhase_ClearsBody()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestBody = "some body";
        msg.RequestLine = "GET /api HTTP/1.1";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal(string.Empty, msg.RequestBody);
        Assert.Equal(string.Empty, msg.RequestLine);
    }

    [Fact]
    public async Task ApplyRulesAsync_Drop_ResponsePhase_ClearsResponse()
    {
        var engine = CreateEngine();
        var msg = MakeResponse(body: "response body");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response,
            Action = ManipulationRuleAction.Drop
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal(string.Empty, msg.ResponseBody);
        Assert.Equal(string.Empty, msg.StatusLine);
    }

    // ── Delay action ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_Delay_ReturnsFalse_BodyUnchanged()
    {
        // Delay doesn't modify the message, returns false
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestBody = "unchanged";
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Request,
            Action = ManipulationRuleAction.Delay,
            DelayMs = 1 // 1ms delay to keep test fast
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
        Assert.Equal("unchanged", msg.RequestBody);
    }

    // ── Multiple rules ───────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyRulesAsync_MultipleRules_AllApplied()
    {
        var engine = CreateEngine();
        var msg = MakeRequest();
        msg.RequestHeaders = "Content-Type: application/json";
        msg.RequestBody = "password=secret";

        var rules = new List<ManipulationRule>
        {
            new()
            {
                Phase = ManipulationPhase.Request,
                Action = ManipulationRuleAction.ModifyHeader,
                HeaderName = "X-Trace",
                HeaderValue = "1"
            },
            new()
            {
                Phase = ManipulationPhase.Request,
                Action = ManipulationRuleAction.ModifyBody,
                BodyReplace = "secret",
                BodyReplaceWith = "***"
            }
        };

        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, rules, TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Contains("X-Trace: 1", msg.RequestHeaders);
        Assert.Equal("password=***", msg.RequestBody);
    }

    // ── RewriteUrl ───────────────────────────────────────────────────────────

    private static HttpMessage MakeProxiedRequest(string host, string path = "/search", string query = "q=1",
        string method = "GET", string scheme = "https", int port = 443) =>
        new()
        {
            Method = method, Host = host, Port = port, Path = path, Query = query, Scheme = scheme,
            RequestLine = $"{method} {path}{(query.Length > 0 ? "?" + query : "")} HTTP/1.1",
            RequestHeaders = $"Host: {host}\r\nAccept: */*",
        };

    [Fact]
    public async Task RewriteUrl_Host_UpdatesHostAndHostHeader_KeepsPathAndQuery()
    {
        var msg = MakeProxiedRequest("bing.com");
        var rule = new ManipulationRule
        {
            HostPattern = @"^bing\.com$", Action = ManipulationRuleAction.RewriteUrl, RewriteHost = "www.bing.com"
        };

        var modified = await CreateEngine().ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Equal("www.bing.com", msg.Host);
        Assert.Equal("GET /search?q=1 HTTP/1.1", msg.RequestLine);
        Assert.Contains("Host: www.bing.com", msg.RequestHeaders);
        Assert.DoesNotContain("Host: bing.com", msg.RequestHeaders);
        Assert.Contains("Accept: */*", msg.RequestHeaders);
    }

    [Fact]
    public async Task RewriteUrl_HostPatternNotMatching_DoesNothing()
    {
        var msg = MakeProxiedRequest("www.bing.com");
        var rule = new ManipulationRule
        {
            HostPattern = @"^bing\.com$", Action = ManipulationRuleAction.RewriteUrl, RewriteHost = "www.bing.com"
        };

        var modified = await CreateEngine().ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
        Assert.Equal("www.bing.com", msg.Host);
    }

    [Fact]
    public async Task RewriteUrl_ExpandsCaptureGroups_ForHostAndPath()
    {
        var msg = MakeProxiedRequest("api.example.com", path: "/v1/users/42", query: "");
        var rule = new ManipulationRule
        {
            HostPattern = @"^(\w+)\.example\.com$",
            PathPattern = @"^/v1/(.*)$",
            Action = ManipulationRuleAction.RewriteUrl,
            RewriteHost = "$1.staging.example.com",
            RewritePath = "/v2/$1?debug=true"
        };

        await CreateEngine().ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.Equal("api.staging.example.com", msg.Host);
        Assert.Equal("/v2/users/42", msg.Path);
        Assert.Equal("debug=true", msg.Query);
        Assert.Equal("GET /v2/users/42?debug=true HTTP/1.1", msg.RequestLine);
    }

    [Fact]
    public async Task RewriteUrl_NonDefaultPort_IncludedInHostHeader()
    {
        var msg = MakeProxiedRequest("example.com", scheme: "http", port: 80);
        var rule = new ManipulationRule
        {
            Action = ManipulationRuleAction.RewriteUrl, RewriteHost = "localhost", RewritePort = 8080
        };

        await CreateEngine().ApplyRulesAsync(msg, ManipulationPhase.Request, [rule], TestContext.Current.CancellationToken);

        Assert.Equal(8080, msg.Port);
        Assert.Contains("Host: localhost:8080", msg.RequestHeaders);
    }

    [Fact]
    public async Task RewriteUrl_InResponsePhase_IsIgnored()
    {
        var msg = MakeProxiedRequest("bing.com");
        var rule = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, Action = ManipulationRuleAction.RewriteUrl, RewriteHost = "www.bing.com"
        };

        var modified = await CreateEngine().ApplyRulesAsync(msg, ManipulationPhase.Response, [rule], TestContext.Current.CancellationToken);

        Assert.False(modified);
        Assert.Equal("bing.com", msg.Host);
    }

    // ── Chaining ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChainedResponseRule_FiresAfterParentRewrite_EvenThoughHostChanged()
    {
        var engine = CreateEngine();
        var msg = MakeProxiedRequest("bing.com");
        var rewrite = new ManipulationRule
        {
            HostPattern = @"^bing\.com$", Action = ManipulationRuleAction.RewriteUrl, RewriteHost = "www.bing.com"
        };
        // Same host pattern as the parent: would NOT match the rewritten host on its own.
        var header = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, ParentRuleId = rewrite.Id,
            Action = ManipulationRuleAction.ModifyHeader, HeaderName = "X-Rewritten", HeaderValue = "1"
        };
        IReadOnlyList<ManipulationRule> rules = [rewrite, header];

        await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, rules, TestContext.Current.CancellationToken);
        msg.StatusLine = "HTTP/1.1 200 OK";
        msg.ResponseHeaders = "Content-Type: text/html";
        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, rules, TestContext.Current.CancellationToken);

        Assert.True(modified);
        Assert.Contains("X-Rewritten: 1", msg.ResponseHeaders);
    }

    [Fact]
    public async Task ChainedRule_ParentDidNotMatch_DoesNotFire()
    {
        var engine = CreateEngine();
        var msg = MakeProxiedRequest("duckduckgo.com");
        var parent = new ManipulationRule
        {
            HostPattern = @"^bing\.com$", Action = ManipulationRuleAction.MatchOnly
        };
        var child = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, ParentRuleId = parent.Id,
            Action = ManipulationRuleAction.ModifyHeader, HeaderName = "X-Hit", HeaderValue = "1"
        };
        IReadOnlyList<ManipulationRule> rules = [parent, child];

        await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, rules, TestContext.Current.CancellationToken);
        msg.ResponseHeaders = "Content-Type: text/html";
        var modified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, rules, TestContext.Current.CancellationToken);

        Assert.False(modified);
        Assert.DoesNotContain("X-Hit", msg.ResponseHeaders);
    }

    [Fact]
    public async Task MatchOnlyTrigger_ChainsStatusAndHeaderResponseRules()
    {
        // POST yandex.net/.*  →  response 200 + X-Custom-Header: MyValue
        var engine = CreateEngine();
        var msg = MakeProxiedRequest("mc.yandex.net", path: "/watch/123", query: "", method: "POST");
        var trigger = new ManipulationRule
        {
            MethodPattern = "^POST$", HostPattern = @"yandex\.net$", PathPattern = "/.*",
            Action = ManipulationRuleAction.MatchOnly
        };
        var status = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, ParentRuleId = trigger.Id,
            Action = ManipulationRuleAction.OverrideStatusCode, OverrideStatusCode = 200
        };
        var header = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, ParentRuleId = trigger.Id,
            Action = ManipulationRuleAction.ModifyHeader, HeaderName = "X-Custom-Header", HeaderValue = "MyValue"
        };
        IReadOnlyList<ManipulationRule> rules = [trigger, status, header];

        var reqModified = await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, rules, TestContext.Current.CancellationToken);
        msg.StatusCode = 403;
        msg.StatusLine = "HTTP/1.1 403 Forbidden";
        msg.ResponseHeaders = "Content-Type: text/plain";
        await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, rules, TestContext.Current.CancellationToken);

        Assert.False(reqModified); // MatchOnly changes nothing on the request
        Assert.Contains(trigger.Id, msg.MatchedRuleIds);
        Assert.Equal(200, msg.StatusCode);
        Assert.Contains("X-Custom-Header: MyValue", msg.ResponseHeaders);
    }

    [Fact]
    public async Task MatchOnlyTrigger_GetRequest_DoesNotTriggerChain()
    {
        var engine = CreateEngine();
        var msg = MakeProxiedRequest("mc.yandex.net", method: "GET");
        var trigger = new ManipulationRule
        {
            MethodPattern = "^POST$", HostPattern = @"yandex\.net$", Action = ManipulationRuleAction.MatchOnly
        };
        var header = new ManipulationRule
        {
            Phase = ManipulationPhase.Response, ParentRuleId = trigger.Id,
            Action = ManipulationRuleAction.ModifyHeader, HeaderName = "X-Custom-Header", HeaderValue = "MyValue"
        };
        IReadOnlyList<ManipulationRule> rules = [trigger, header];

        await engine.ApplyRulesAsync(msg, ManipulationPhase.Request, rules, TestContext.Current.CancellationToken);
        msg.ResponseHeaders = "Content-Type: text/plain";
        await engine.ApplyRulesAsync(msg, ManipulationPhase.Response, rules, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("X-Custom-Header", msg.ResponseHeaders);
    }
}
