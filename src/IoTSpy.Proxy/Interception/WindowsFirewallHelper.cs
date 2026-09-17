using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Proxy.Interception;

public sealed record FirewallRule(int Port, string Protocol, string Label);

public sealed record FirewallRuleResult(int Port, string Protocol, string Label, bool Success, string Message);

public interface IWindowsFirewallHelper
{
    /// <summary>Adds one Windows Defender Firewall inbound-allow rule per entry via netsh. Requires the process to be elevated.</summary>
    Task<IReadOnlyList<FirewallRuleResult>> ConfigureRulesAsync(IEnumerable<FirewallRule> rules);
}

/// <summary>
/// Manages Windows Defender Firewall inbound-allow rules for IoTSpy's listening ports.
/// Mirrors <see cref="IptablesHelper"/>'s subprocess pattern for the Windows platform.
/// </summary>
public sealed class WindowsFirewallHelper(ILogger<WindowsFirewallHelper> logger) : IWindowsFirewallHelper
{
    public async Task<IReadOnlyList<FirewallRuleResult>> ConfigureRulesAsync(IEnumerable<FirewallRule> rules)
    {
        var results = new List<FirewallRuleResult>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger.LogWarning("Windows Firewall configuration is only supported on Windows");
            foreach (var rule in rules)
                results.Add(new FirewallRuleResult(rule.Port, rule.Protocol, rule.Label, false, "Not running on Windows"));
            return results;
        }

        foreach (var rule in rules)
        {
            var ruleName = $"IoTSpy - {rule.Label}";
            var args = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol={rule.Protocol} localport={rule.Port}";
            var (success, message) = await RunCommandAsync("netsh", args);
            results.Add(new FirewallRuleResult(rule.Port, rule.Protocol, rule.Label, success, message));

            if (success)
                logger.LogInformation("Firewall rule added: {RuleName} ({Protocol}/{Port})", ruleName, rule.Protocol, rule.Port);
            else
                logger.LogWarning("Failed to add firewall rule {RuleName}: {Message}", ruleName, message);
        }

        return results;
    }

    private static async Task<(bool Success, string Message)> RunCommandAsync(string command, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return (false, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());

            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
