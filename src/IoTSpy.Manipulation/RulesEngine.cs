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

            ManipulationMetrics.RecordRuleMatch(rule.Id);
            logger.LogDebug("Rule {RuleName} matched {Host}{Path}", rule.Name, message.Host, message.Path);

            var applied = await ApplyActionAsync(rule, message, phase, ct);
            if (applied) modified = true;
        }

        return modified;
    }

    internal static bool Matches(ManipulationRule rule, HttpMessage message)
    {
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
