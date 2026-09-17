using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public class DevicesController(IDeviceRepository devices) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var allItems = await devices.GetAllAsync(ct);
        var items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Ok(new { items, total = allItems.Count, page, pageSize, pages = (int)Math.Ceiling(allItems.Count / (double)pageSize) });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var device = await devices.GetByIdAsync(id);
        return device is null ? NotFound() : Ok(device);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DevicePatchDto dto)
    {
        var device = await devices.GetByIdAsync(id);
        if (device is null) return NotFound();

        if (dto.Label is not null) device.Label = dto.Label;
        if (dto.Notes is not null) device.Notes = dto.Notes;
        if (dto.Tags is not null) device.Tags = dto.Tags;
        if (dto.InterceptionEnabled.HasValue) device.InterceptionEnabled = dto.InterceptionEnabled.Value;

        return Ok(await devices.UpdateAsync(device));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await devices.DeleteAsync(id);
        return NoContent();
    }
}

public record DevicePatchDto(
    string? Label,
    string? Notes,
    bool? InterceptionEnabled,
    string? Tags = null
);
