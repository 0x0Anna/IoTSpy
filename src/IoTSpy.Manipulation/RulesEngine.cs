using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Manipulation;

/// <summary>
/// Evaluates declarative manipulation rules against HTTP messages.
/// Each rule has match criteria and an action (modify header, modify body, override status, drop, delay).
/// </summary>
public class RulesEngine(ILogger<RulesEngine> logger)
{
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> RegexCache = new();

    internal static Regex GetRegex(string pattern, RegexOptions options) =>
        RegexCache.GetOrAdd((pattern, options), k => new Regex(k.Pattern, k.Options | RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

    /// <summary>
    /// Apply all matching rules in priority order. Returns true if any rule modified the message.
    /// </summary>
    public async Task<bool> ApplyRulesAsync(
        HttpMessage message, ManipulationPhase phase,
        IReadOnlyList<ManipulationRule> rules, CancellationToken ct = default)
    {
        var modified = false;

        foreach (var rule in rules.Where(r => r.Phase == phase))
        {
            ct.ThrowIfCancellationRequested();

            if (!Matches(rule, message))
                continue;

            message.MatchedRuleIds.Add(rule.Id);
            ManipulationMetrics.RecordRuleMatch(rule.Id);
            logger.LogDebug("Rule {RuleName} matched {Host}{Path}", rule.Name, message.Host, message.Path);

            var applied = await ApplyActionAsync(rule, message, phase, ct);
            if (applied) modified = true;
        }

        return modified;
    }

    /// <summary>
    /// Request-phase Delay rules (in priority order) matching a connection described as a CONNECT
    /// message, up to the first matching Drop rule. Chained rules never match here: no other rule
    /// has run on the connection.
    /// </summary>
    internal static ConnectionRuleMatch MatchConnectionRules(IReadOnlyList<ManipulationRule> rules, HttpMessage connection)
    {
        var delays = new List<ManipulationRule>();
        foreach (var rule in rules)
        {
            if (rule.Phase != ManipulationPhase.Request) continue;
            if (rule.Action is not (ManipulationRuleAction.Drop or ManipulationRuleAction.Delay)) continue;
            if (!Matches(rule, connection)) continue;

            if (rule.Action == ManipulationRuleAction.Drop) return new ConnectionRuleMatch(delays, rule);
            if (rule.DelayMs is > 0) delays.Add(rule);
        }
        return delays.Count == 0 ? ConnectionRuleMatch.None : new ConnectionRuleMatch(delays, null);
    }

    internal static bool Matches(ManipulationRule rule, HttpMessage message)
    {
        // Chained rule: only fires when its parent already matched this exchange.
        if (rule.ParentRuleId is { } parentId && !message.MatchedRuleIds.Contains(parentId))
            return false;

        if (rule.HostPattern is not null && !GetRegex(rule.HostPattern, RegexOptions.IgnoreCase).IsMatch(message.Host))
            return false;

        if (rule.PathPattern is not null && !GetRegex(rule.PathPattern, RegexOptions.IgnoreCase).IsMatch(message.Path))
            return false;

        if (rule.MethodPattern is not null && !GetRegex(rule.MethodPattern, RegexOptions.IgnoreCase).IsMatch(message.Method))
            return false;

        return true;
    }

    private static async Task<bool> ApplyActionAsync(
        ManipulationRule rule, HttpMessage message, ManipulationPhase phase, CancellationToken ct)
    {
        switch (rule.Action)
        {
            case ManipulationRuleAction.ModifyHeader:
                return ApplyModifyHeader(rule, message, phase);

            case ManipulationRuleAction.ModifyBody:
                return ApplyModifyBody(rule, message, phase);

            case ManipulationRuleAction.OverrideStatusCode:
                if (phase == ManipulationPhase.Response && rule.OverrideStatusCode.HasValue)
                {
                    message.StatusCode = rule.OverrideStatusCode.Value;
                    // Rewrite the status line
                    message.StatusLine = $"HTTP/1.1 {rule.OverrideStatusCode.Value} Modified";
                    return true;
                }
                return false;

            case ManipulationRuleAction.RewriteUrl:
                return phase == ManipulationPhase.Request && ApplyRewriteUrl(rule, message);

            case ManipulationRuleAction.MatchOnly:
                return false; // trigger for chained rules only

            case ManipulationRuleAction.Delay:
                if (rule.DelayMs is > 0)
                    await Task.Delay(rule.DelayMs.Value, ct);
                return false; // delay doesn't modify content

            case ManipulationRuleAction.Drop:
                // Drop is signaled by clearing the body and returning modified
                if (phase == ManipulationPhase.Request)
                {
                    message.RequestBody = string.Empty;
                    message.RequestLine = string.Empty;
                }
                else
                {
                    message.ResponseBody = string.Empty;
                    message.StatusLine = string.Empty;
                }
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Rewrites host/port/path+query of the outgoing request. Each rewrite target is expanded
    /// against the corresponding pattern's match (so "$1" etc. work); with no pattern it is used
    /// literally. Keeps <see cref="HttpMessage.RequestLine"/> and the Host header consistent with
    /// the new target — the proxy derives the upstream connection from Host/Port.
    /// </summary>
    internal static bool ApplyRewriteUrl(ManipulationRule rule, HttpMessage message)
    {
        if (string.IsNullOrEmpty(message.RequestLine)) return false; // dropped

        var newHost = message.Host;
        var newPort = message.Port;
        var newPath = message.Path;
        var newQuery = message.Query;

        if (!string.IsNullOrWhiteSpace(rule.RewriteHost))
            newHost = Expand(rule.HostPattern, message.Host, rule.RewriteHost.Trim());

        if (rule.RewritePort is > 0 and <= 65535)
            newPort = rule.RewritePort.Value;

        if (!string.IsNullOrWhiteSpace(rule.RewritePath))
        {
            var target = Expand(rule.PathPattern, message.Path, rule.RewritePath.Trim());
            var q = target.IndexOf('?');
            if (q >= 0)
            {
                newPath = target[..q];
                newQuery = target[(q + 1)..];
            }
            else
            {
                newPath = target;
            }
            if (!newPath.StartsWith('/')) newPath = "/" + newPath;
        }

        if (string.IsNullOrWhiteSpace(newHost)) return false;

        if (newHost == message.Host && newPort == message.Port && newPath == message.Path && newQuery == message.Query)
            return false;

        message.Host = newHost;
        message.Port = newPort;
        message.Path = newPath;
        message.Query = newQuery;

        var lastSpace = message.RequestLine.LastIndexOf(' ');
        var version = lastSpace >= 0 ? message.RequestLine[(lastSpace + 1)..] : "HTTP/1.1";
        message.RequestLine = $"{message.Method} {newPath}{(string.IsNullOrEmpty(newQuery) ? "" : "?" + newQuery)} {version}";

        var defaultPort = message.Scheme == "https" ? 443 : 80;
        var hostHeader = newPort == defaultPort || newPort == 0 ? newHost : $"{newHost}:{newPort}";
        message.RequestHeaders = SetHeader(message.RequestHeaders, "Host", hostHeader);

        return true;
    }

    private static string Expand(string? pattern, string input, string replacement)
    {
        if (string.IsNullOrEmpty(pattern)) return replacement;
        var match = GetRegex(pattern, RegexOptions.IgnoreCase).Match(input);
        return match.Success ? match.Result(replacement) : replacement;
    }

    private static string SetHeader(string headers, string name, string value)
    {
        var lines = headers.Split("\r\n").ToList();
        var idx = lines.FindIndex(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) lines[idx] = $"{name}: {value}";
        else if (lines.Count > 0 && lines[0].Length == 0) lines[0] = $"{name}: {value}";
        else lines.Insert(0, $"{name}: {value}");
        return string.Join("\r\n", lines);
    }

    private static bool ApplyModifyHeader(ManipulationRule rule, HttpMessage message, ManipulationPhase phase)
    {
        if (rule.HeaderName is null) return false;

        var headers = phase == ManipulationPhase.Request
            ? message.RequestHeaders
            : message.ResponseHeaders;

        var headerLines = headers.Split("\r\n").ToList();
        var found = false;

        for (var i = headerLines.Count - 1; i >= 0; i--)
        {
            if (headerLines[i].StartsWith(rule.HeaderName + ":", StringComparison.OrdinalIgnoreCase))
            {
                if (rule.HeaderValue is null)
                    headerLines.RemoveAt(i); // remove header
                else
                    headerLines[i] = $"{rule.HeaderName}: {rule.HeaderValue}"; // replace
                found = true;
            }
        }

        // Add header if not found and we have a value
        if (!found && rule.HeaderValue is not null)
            headerLines.Add($"{rule.HeaderName}: {rule.HeaderValue}");

        var result = string.Join("\r\n", headerLines);
        if (phase == ManipulationPhase.Request)
            message.RequestHeaders = result;
        else
            message.ResponseHeaders = result;

        return true;
    }

    private static bool ApplyModifyBody(ManipulationRule rule, HttpMessage message, ManipulationPhase phase)
    {
        if (rule.BodyReplace is null) return false;

        var body = phase == ManipulationPhase.Request
            ? message.RequestBody
            : message.ResponseBody;

        var newBody = GetRegex(rule.BodyReplace, RegexOptions.None).Replace(body, rule.BodyReplaceWith ?? string.Empty);
        if (newBody == body) return false;

        if (phase == ManipulationPhase.Request)
            message.RequestBody = newBody;
        else
            message.ResponseBody = newBody;

        return true;
    }
}
