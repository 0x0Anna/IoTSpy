using IoTSpy.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IoTSpy.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public class ReportController(
    IReportService reportService,
    IDeviceRepository devices,
    IInvestigationSessionRepository sessions) : ControllerBase
{
    [HttpGet("devices/{deviceId:guid}/html")]
    public async Task<IActionResult> GetDeviceHtmlReport(Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound();

        var html = await reportService.GenerateDeviceHtmlReportAsync(deviceId, ct);
        return File(html, "text/html", $"scan-report-{deviceId}.html");
    }

    [HttpGet("devices/{deviceId:guid}/pdf")]
    public async Task<IActionResult> GetDevicePdfReport(Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetByIdAsync(deviceId, ct);
        if (device is null) return NotFound();

        var pdf = await reportService.GenerateDevicePdfReportAsync(deviceId, ct);
        return File(pdf, "application/pdf", $"scan-report-{deviceId}.pdf");
    }

    [HttpGet("sessions/{sessionId:guid}/html")]
    public async Task<IActionResult> GetSessionHtmlReport(Guid sessionId, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null) return NotFound();

        var html = await reportService.GenerateSessionHtmlReportAsync(sessionId, ct);
        return File(html, "text/html", $"session-report-{sessionId}.html");
    }

    [HttpGet("sessions/{sessionId:guid}/pdf")]
    public async Task<IActionResult> GetSessionPdfReport(Guid sessionId, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null) return NotFound();

        var pdf = await reportService.GenerateSessionPdfReportAsync(sessionId, ct);
        return File(pdf, "application/pdf", $"session-report-{sessionId}.pdf");
    }
}
