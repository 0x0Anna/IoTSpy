using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/proxy")]
public class ProxyController(IProxyService proxy) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        isRunning = proxy.IsRunning,
        port = proxy.Port,
        settings = proxy.GetSettings()
    });

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        await proxy.StartAsync();
        return Ok(new { isRunning = proxy.IsRunning, port = proxy.Port });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        await proxy.StopAsync();
        return Ok(new { isRunning = proxy.IsRunning });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateProxySettingsDto dto, CancellationToken ct)
    {
        var settings = proxy.GetSettings();
        settings.ProxyPort = dto.ProxyPort ?? settings.ProxyPort;
        settings.Mode = dto.Mode ?? settings.Mode;
        settings.CaptureTls = dto.CaptureTls ?? settings.CaptureTls;
        settings.CaptureRequestBodies = dto.CaptureRequestBodies ?? settings.CaptureRequestBodies;
        settings.CaptureResponseBodies = dto.CaptureResponseBodies ?? settings.CaptureResponseBodies;
        settings.MaxBodySizeKb = dto.MaxBodySizeKb ?? settings.MaxBodySizeKb;
        settings.ListenAddress = dto.ListenAddress ?? settings.ListenAddress;
        settings.TransparentProxyPort = dto.TransparentProxyPort ?? settings.TransparentProxyPort;
        settings.TargetDeviceIp = dto.TargetDeviceIp ?? settings.TargetDeviceIp;
        settings.GatewayIp = dto.GatewayIp ?? settings.GatewayIp;
        settings.NetworkInterface = dto.NetworkInterface ?? settings.NetworkInterface;
        settings.SslStrip = dto.SslStrip ?? settings.SslStrip;
        settings.AutoStart = dto.AutoStart ?? settings.AutoStart;
        if (dto.TlsPassthroughHosts is not null)
        {
            if (dto.TlsPassthroughHosts.Length > 16_384)
                return BadRequest(new { error = "tlsPassthroughHosts is too long (max 16384 characters)." });
            settings.TlsPassthroughHosts = dto.TlsPassthroughHosts.Trim();
        }
        settings.AutoPassthroughOnTlsFailure = dto.AutoPassthroughOnTlsFailure ?? settings.AutoPassthroughOnTlsFailure;
        if (!string.IsNullOrWhiteSpace(dto.CaCommonName)) settings.CaCommonName = dto.CaCommonName;
        if (!string.IsNullOrWhiteSpace(dto.CaOrganization)) settings.CaOrganization = dto.CaOrganization;
        if (!string.IsNullOrWhiteSpace(dto.CaCountry)) settings.CaCountry = dto.CaCountry;
        if (dto.CaValidityYears is > 0 and <= 30) settings.CaValidityYears = dto.CaValidityYears.Value;
        // Passive is independent of the transport mode. A legacy client sending Mode = Passive is
        // normalized to explicit proxy + IsPassive (by NormalizeLegacyPassiveMode on save).
        settings.NormalizeLegacyPassiveMode();
        settings.IsPassive = dto.IsPassive ?? settings.IsPassive;
        await proxy.UpdateSettingsAsync(settings, ct);
        return Ok(proxy.GetSettings());
    }
}

public record UpdateProxySettingsDto(
    int? ProxyPort = null,
    ProxyMode? Mode = null,
    bool? CaptureTls = null,
    bool? CaptureRequestBodies = null,
    bool? CaptureResponseBodies = null,
    int? MaxBodySizeKb = null,
    string? ListenAddress = null,
    int? TransparentProxyPort = null,
    string? TargetDeviceIp = null,
    string? GatewayIp = null,
    string? NetworkInterface = null,
    bool? SslStrip = null,
    bool? AutoStart = null,
    bool? IsPassive = null,
    string? CaCommonName = null,
    string? CaOrganization = null,
    string? CaCountry = null,
    int? CaValidityYears = null,
    string? TlsPassthroughHosts = null,
    bool? AutoPassthroughOnTlsFailure = null
);
