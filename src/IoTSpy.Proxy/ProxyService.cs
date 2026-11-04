using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Proxy.Interception;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Proxy;

public class ProxyService(
    ExplicitProxyServer explicitProxy,
    TransparentProxyServer transparentProxy,
    IptablesHelper iptablesHelper,
    ArpSpoofEngine arpSpoofEngine,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ProxyService> logger) : IProxyService, IHostedService
{
    private ProxySettings? _settings;

    public bool IsRunning => explicitProxy.IsRunning || transparentProxy.IsRunning;
    public int Port => _settings?.ProxyPort ?? 8888;

    public ProxySettings GetSettings() => _settings ?? new ProxySettings();

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
        _settings = await settingsRepo.GetAsync(ct);
        _settings.IsRunning = true;
        await settingsRepo.SaveAsync(_settings, ct);

        await StartByModeAsync(_settings, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await StopAllAsync();
        if (_settings is not null)
        {
            _settings.IsRunning = false;
            using var scope = scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
            await settingsRepo.SaveAsync(_settings, ct);
        }
    }

    public async Task UpdateSettingsAsync(ProxySettings settings, CancellationToken ct = default)
    {
        var wasRunning = IsRunning;
        if (wasRunning) await StopAllAsync();

        using var scope = scopeFactory.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
        _settings = await settingsRepo.SaveAsync(settings, ct);

        if (settings.IsRunning)
            await StartByModeAsync(settings, ct);
    }

    async Task IHostedService.StartAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
        _settings = await settingsRepo.GetAsync(ct);
        if (_settings.AutoStart || _settings.IsRunning)
        {
            await StartByModeAsync(_settings, ct);
            logger.LogInformation("ProxyService auto-started in {Mode} mode", _settings.Mode);
        }
    }

    Task IHostedService.StopAsync(CancellationToken ct) => StopAsync(ct);

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task StartByModeAsync(ProxySettings settings, CancellationToken ct)
    {
        // Passive (observe-only) is a flag on top of the transport, not a transport itself: the
        // listeners read it per connection, so GatewayRedirect keeps its iptables redirect.
        settings.NormalizeLegacyPassiveMode();
        var observe = settings.IsPassive ? " (passive, observe-only)" : string.Empty;
        switch (settings.Mode)
        {
            case ProxyMode.ExplicitProxy:
                await explicitProxy.StartAsync(settings.ProxyPort, settings.ListenAddress, ct);
                logger.LogInformation("Explicit proxy started on port {Port}{Observe}", settings.ProxyPort, observe);
                break;

            case ProxyMode.GatewayRedirect:
                await transparentProxy.StartAsync(settings.TransparentProxyPort, settings.ListenAddress, ct);
                await iptablesHelper.InstallRedirectRulesAsync(settings.TransparentProxyPort, inboundInterface: RedirectInterface(settings));
                logger.LogInformation("GatewayRedirect mode started on port {Port}{Observe}", settings.TransparentProxyPort, observe);
                break;

            case ProxyMode.ArpSpoof:
                await transparentProxy.StartAsync(settings.TransparentProxyPort, settings.ListenAddress, ct);
                await iptablesHelper.InstallRedirectRulesAsync(settings.TransparentProxyPort, inboundInterface: RedirectInterface(settings));
                await arpSpoofEngine.StartAsync(
                    settings.TargetDeviceIp,
                    settings.GatewayIp,
                    settings.NetworkInterface, ct);
                logger.LogInformation("ArpSpoof mode started targeting {Target} via {Gateway}{Observe}",
                    settings.TargetDeviceIp, settings.GatewayIp, observe);
                break;
        }
    }

    /// <summary>
    /// The interface redirect rules are limited to: the persisted <c>NetworkInterface</c> setting, or
    /// the deployment-level <c>Proxy:RedirectInterface</c> (env <c>Proxy__RedirectInterface</c>) so a
    /// gateway host can pin it without editing settings. Null means all interfaces.
    /// </summary>
    private string? RedirectInterface(ProxySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.NetworkInterface)) return settings.NetworkInterface;
        var configured = configuration["Proxy:RedirectInterface"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private async Task StopAllAsync()
    {
        await arpSpoofEngine.StopAsync();
        await iptablesHelper.RemoveRedirectRulesAsync();
        await transparentProxy.StopAsync();
        await explicitProxy.StopAsync();
    }
}
