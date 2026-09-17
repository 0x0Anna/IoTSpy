using IoTSpy.Core.Interfaces;
using IoTSpy.Scanner.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QuestPDF.Infrastructure;
using System.Text;

namespace IoTSpy.Scanner;

/// <summary>
/// Generates HTML and PDF reports, scoped either to a device (scan findings + traffic history)
/// or an investigation session (adds annotations and the session's activity feed). Data loading
/// lives in <see cref="ReportDataLoader"/>, HTML rendering in <see cref="ReportTemplateEngine"/>
/// (Scriban), and PDF rendering in <see cref="ReportPdfBuilder"/> (QuestPDF) — this class just
/// wires the three together per scope/format combination.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportService> _logger;

    public ReportService(IServiceScopeFactory scopeFactory, ILogger<ReportService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateDeviceHtmlReportAsync(Guid deviceId, CancellationToken ct = default)
    {
        var data = await ReportDataLoader.LoadForDeviceAsync(_scopeFactory, deviceId, ct);
        return Encoding.UTF8.GetBytes(ReportTemplateEngine.Render(data));
    }

    public async Task<byte[]> GenerateDevicePdfReportAsync(Guid deviceId, CancellationToken ct = default)
    {
        var data = await ReportDataLoader.LoadForDeviceAsync(_scopeFactory, deviceId, ct);
        return ReportPdfBuilder.Build(data);
    }

    public async Task<byte[]> GenerateSessionHtmlReportAsync(Guid sessionId, CancellationToken ct = default)
    {
        var data = await ReportDataLoader.LoadForSessionAsync(_scopeFactory, sessionId, ct);
        return Encoding.UTF8.GetBytes(ReportTemplateEngine.Render(data));
    }

    public async Task<byte[]> GenerateSessionPdfReportAsync(Guid sessionId, CancellationToken ct = default)
    {
        var data = await ReportDataLoader.LoadForSessionAsync(_scopeFactory, sessionId, ct);
        return ReportPdfBuilder.Build(data);
    }
}
