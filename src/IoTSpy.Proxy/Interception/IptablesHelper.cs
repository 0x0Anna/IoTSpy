using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Manages iptables REDIRECT rules for GatewayRedirect mode.
/// Requires root/CAP_NET_ADMIN to execute iptables commands.
/// </summary>
public sealed class IptablesHelper(ILogger<IptablesHelper> logger)
{
    private readonly List<string> _activeRules = [];

    /// <summary>Default ports to redirect through the transparent proxy.</summary>
    public static readonly int[] DefaultRedirectPorts = [80, 443, 1883, 8883];

    /// <summary>
    /// Installs iptables REDIRECT rules for the given destination ports.
    /// Traffic to these ports on the PREROUTING chain will be redirected to the transparent proxy port.
    /// When <paramref name="inboundInterface"/> is set the rules only match traffic arriving on that
    /// interface (for example the capture-side NIC of a gateway), so the host's other interfaces are
    /// not affected. Rules that already exist are left alone, so a restart after an unclean exit does
    /// not stack duplicates.
    /// </summary>
    public async Task<bool> InstallRedirectRulesAsync(int transparentProxyPort, int[]? ports = null, string? inboundInterface = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger.LogWarning("iptables rules only supported on Linux");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(inboundInterface) && !IsValidInterfaceName(inboundInterface))
        {
            logger.LogError("Refusing to install iptables rules: {Interface} is not a valid interface name", inboundInterface);
            return false;
        }

        ports ??= DefaultRedirectPorts;

        // Enable IP forwarding
        if (!await RunCommandAsync("sysctl", "-w net.ipv4.ip_forward=1"))
            logger.LogWarning("Failed to enable IP forwarding — GatewayRedirect may not work");

        foreach (var port in ports)
        {
            var rule = BuildRedirectRule(port, transparentProxyPort, inboundInterface);
            if (await RunCommandAsync("iptables", $"-t nat -C {rule}"))
            {
                // Already present (left over from an unclean exit). Track it so Remove still cleans it up.
                _activeRules.Add(rule);
                logger.LogInformation("iptables rule already present: {Rule}", rule);
            }
            else if (await RunCommandAsync("iptables", $"-t nat -A {rule}"))
            {
                _activeRules.Add(rule);
                logger.LogInformation("iptables rule installed: {Rule}", rule);
            }
            else
            {
                logger.LogError("Failed to install iptables rule: {Rule}", rule);
            }
        }

        return _activeRules.Count > 0;
    }

    /// <summary>The rule body shared by -C, -A and -D, e.g. <c>PREROUTING -i eth1 -p tcp --dport 443 -j REDIRECT --to-port 9999</c>.</summary>
    public static string BuildRedirectRule(int destinationPort, int proxyPort, string? inboundInterface = null)
    {
        var iface = string.IsNullOrWhiteSpace(inboundInterface) ? "" : $"-i {inboundInterface} ";
        return $"PREROUTING {iface}-p tcp --dport {destinationPort} -j REDIRECT --to-port {proxyPort}";
    }

    /// <summary>
    /// Linux interface names are at most 15 characters. The name is placed on an iptables command
    /// line, so anything outside this conservative set is rejected.
    /// </summary>
    public static bool IsValidInterfaceName(string name) =>
        name.Length is > 0 and <= 15 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>
    /// Removes all iptables REDIRECT rules that were installed by this helper.
    /// </summary>
    public async Task RemoveRedirectRulesAsync()
    {
        foreach (var rule in _activeRules)
        {
            var deleteArgs = $"-t nat -D {rule}";
            if (await RunCommandAsync("iptables", deleteArgs))
                logger.LogInformation("iptables rule removed: {Args}", deleteArgs);
            else
                logger.LogWarning("Failed to remove iptables rule: {Args}", deleteArgs);
        }
        _activeRules.Clear();
    }

    private async Task<bool> RunCommandAsync(string command, string arguments)
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
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                logger.LogDebug("{Command} {Args} failed: {Error}", command, arguments, stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to run {Command} {Args}", command, arguments);
            return false;
        }
    }
}
