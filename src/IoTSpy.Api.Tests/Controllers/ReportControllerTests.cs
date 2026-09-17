using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class ReportControllerTests
{
    private readonly IReportService _reportService = Substitute.For<IReportService>();
    private readonly IDeviceRepository _devices = Substitute.For<IDeviceRepository>();
    private readonly IInvestigationSessionRepository _sessions = Substitute.For<IInvestigationSessionRepository>();

    private ReportController CreateController() => new(_reportService, _devices, _sessions);

    [Fact]
    public async Task GetDeviceHtmlReport_UnknownDevice_ReturnsNotFound()
    {
        _devices.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Device?)null);

        var result = await CreateController().GetDeviceHtmlReport(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetDeviceHtmlReport_KnownDevice_ReturnsHtmlFile()
    {
        var device = new Device { Id = Guid.NewGuid() };
        _devices.GetByIdAsync(device.Id, Arg.Any<CancellationToken>()).Returns(device);
        _reportService.GenerateDeviceHtmlReportAsync(device.Id, Arg.Any<CancellationToken>())
            .Returns("<html></html>"u8.ToArray());

        var result = await CreateController().GetDeviceHtmlReport(device.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/html", file.ContentType);
    }

    [Fact]
    public async Task GetDevicePdfReport_UnknownDevice_ReturnsNotFound()
    {
        _devices.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Device?)null);

        var result = await CreateController().GetDevicePdfReport(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSessionHtmlReport_UnknownSession_ReturnsNotFound()
    {
        _sessions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((InvestigationSession?)null);

        var result = await CreateController().GetSessionHtmlReport(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSessionHtmlReport_KnownSession_ReturnsHtmlFile()
    {
        var session = new InvestigationSession { Id = Guid.NewGuid(), Name = "Test Session" };
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _reportService.GenerateSessionHtmlReportAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns("<html></html>"u8.ToArray());

        var result = await CreateController().GetSessionHtmlReport(session.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/html", file.ContentType);
    }

    [Fact]
    public async Task GetSessionPdfReport_UnknownSession_ReturnsNotFound()
    {
        _sessions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((InvestigationSession?)null);

        var result = await CreateController().GetSessionPdfReport(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetSessionPdfReport_KnownSession_ReturnsPdfFile()
    {
        var session = new InvestigationSession { Id = Guid.NewGuid() };
        _sessions.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _reportService.GenerateSessionPdfReportAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns([0x25, 0x50, 0x44, 0x46]);

        var result = await CreateController().GetSessionPdfReport(session.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
    }
}
