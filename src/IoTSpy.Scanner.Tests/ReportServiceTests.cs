using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text;
using Xunit;

namespace IoTSpy.Scanner.Tests;

public class ReportServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(
        Device? device = null,
        List<ScanJob>? jobs = null,
        List<ScanFinding>? findings = null,
        List<CapturedRequest>? captures = null,
        List<PersistedProtocolMessage>? protocolMessages = null,
        InvestigationSession? session = null,
        List<SessionCapture>? sessionCaptures = null,
        List<CaptureAnnotation>? annotations = null,
        List<SessionActivity>? activities = null)
    {
        jobs ??= [];
        findings ??= [];
        captures ??= [];
        protocolMessages ??= [];
        sessionCaptures ??= [];
        annotations ??= [];
        activities ??= [];

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(device);

        var scanJobRepo = new Mock<IScanJobRepository>();
        scanJobRepo.Setup(r => r.GetByDeviceIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(jobs);
        scanJobRepo.Setup(r => r.GetFindingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((Guid jobId, CancellationToken _) =>
                       findings.Where(f => f.ScanJobId == jobId).ToList());

        var captureRepo = new Mock<ICaptureRepository>();
        captureRepo.Setup(r => r.GetPagedAsync(
                It.IsAny<CaptureFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(captures);

        var protocolMessageRepo = new Mock<IProtocolMessageRepository>();
        protocolMessageRepo.Setup(r => r.GetByDeviceIdAsync(
                It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(protocolMessages);
        protocolMessageRepo.Setup(r => r.GetByDeviceIdsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(protocolMessages);

        var sessionRepo = new Mock<IInvestigationSessionRepository>();
        sessionRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(session);
        sessionRepo.Setup(r => r.GetSessionCapturesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(sessionCaptures);

        var annotationRepo = new Mock<ICaptureAnnotationRepository>();
        annotationRepo.Setup(r => r.GetBySessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(annotations);

        var activityRepo = new Mock<ISessionActivityRepository>();
        activityRepo.Setup(r => r.GetBySessionAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(activities);

        var services = new ServiceCollection();
        services.AddSingleton(deviceRepo.Object);
        services.AddSingleton(scanJobRepo.Object);
        services.AddSingleton(captureRepo.Object);
        services.AddSingleton(protocolMessageRepo.Object);
        services.AddSingleton(sessionRepo.Object);
        services.AddSingleton(annotationRepo.Object);
        services.AddSingleton(activityRepo.Object);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IServiceScopeFactory>();
    }

    private static Device MakeDevice() => new()
    {
        Id = Guid.NewGuid(),
        Label = "TestDevice",
        IpAddress = "192.168.1.1",
        MacAddress = "00:11:22:33:44:55",
        Hostname = "testdevice.local"
    };

    private static ScanFinding MakeFinding(Guid jobId, ScanFindingSeverity severity, string title) => new()
    {
        Id = Guid.NewGuid(),
        ScanJobId = jobId,
        Title = title,
        Description = $"Description for {title}",
        Severity = severity,
        Type = ScanFindingType.OpenPort
    };

    // ── Device-scoped ────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateDeviceHtmlReport_ReturnsHtmlWithFindings()
    {
        var device = MakeDevice();
        var job = new ScanJob { Id = Guid.NewGuid(), DeviceId = device.Id, TargetIp = device.IpAddress };
        var findings = new List<ScanFinding>
        {
            MakeFinding(job.Id, ScanFindingSeverity.Critical, "Critical Issue"),
            MakeFinding(job.Id, ScanFindingSeverity.High, "High Issue"),
            MakeFinding(job.Id, ScanFindingSeverity.Medium, "Medium Issue")
        };

        var scopeFactory = BuildScopeFactory(device, [job], findings);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateDeviceHtmlReportAsync(device.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("TestDevice", html);
        Assert.Contains("Critical Issue", html);
        Assert.Contains("High Issue", html);
        Assert.Contains("Medium Issue", html);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Critical", html);
    }

    [Fact]
    public async Task GenerateDeviceHtmlReport_EmptyFindings_ReturnsEmptyTable()
    {
        var device = MakeDevice();
        var job = new ScanJob { Id = Guid.NewGuid(), DeviceId = device.Id, TargetIp = device.IpAddress };

        var scopeFactory = BuildScopeFactory(device, [job], []);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateDeviceHtmlReportAsync(device.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("No findings", html);
    }

    [Fact]
    public async Task GenerateDeviceHtmlReport_EscapesUserSuppliedText()
    {
        var device = MakeDevice();
        device.Hostname = "<script>alert(1)</script>";
        var job = new ScanJob { Id = Guid.NewGuid(), DeviceId = device.Id, TargetIp = device.IpAddress };
        var findings = new List<ScanFinding> { MakeFinding(job.Id, ScanFindingSeverity.High, "<b>xss</b>") };

        var scopeFactory = BuildScopeFactory(device, [job], findings);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateDeviceHtmlReportAsync(device.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<b>xss</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task GenerateDeviceHtmlReport_IncludesProtocolMessages()
    {
        var device = MakeDevice();
        var messages = new List<PersistedProtocolMessage>
        {
            new()
            {
                DeviceId = device.Id,
                Protocol = InterceptionProtocol.Mqtt,
                Direction = "client→broker",
                Subject = "sensors/temp",
                Summary = "PUBLISH qos=1"
            }
        };

        var scopeFactory = BuildScopeFactory(device, [], [], protocolMessages: messages);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateDeviceHtmlReportAsync(device.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("sensors/temp", html);
        Assert.Contains("PUBLISH qos=1", html);
    }

    [Fact]
    public async Task GenerateDevicePdfReport_ReturnsBytesStartingWithPdfMagic()
    {
        var device = MakeDevice();
        var job = new ScanJob { Id = Guid.NewGuid(), DeviceId = device.Id, TargetIp = device.IpAddress };
        var findings = new List<ScanFinding> { MakeFinding(job.Id, ScanFindingSeverity.High, "High Finding") };

        var scopeFactory = BuildScopeFactory(device, [job], findings);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateDevicePdfReportAsync(device.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }

    // ── Session-scoped ───────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateSessionHtmlReport_IncludesSessionInfoAndAnnotations()
    {
        var session = new InvestigationSession
        {
            Id = Guid.NewGuid(),
            Name = "Investigation Alpha",
            CreatedByUsername = "annalise"
        };
        var deviceId = Guid.NewGuid();
        var capture = new CapturedRequest { Id = Guid.NewGuid(), DeviceId = deviceId, Host = "iot.example.com" };
        var sessionCaptures = new List<SessionCapture>
        {
            new() { SessionId = session.Id, CaptureId = capture.Id, Capture = capture }
        };
        var annotations = new List<CaptureAnnotation>
        {
            new() { SessionId = session.Id, CaptureId = capture.Id, Username = "annalise", Note = "Looks suspicious", Tags = "suspicious,pii" }
        };
        var activities = new List<SessionActivity>
        {
            new() { SessionId = session.Id, Username = "annalise", Action = "started scan" }
        };

        var scopeFactory = BuildScopeFactory(
            session: session, sessionCaptures: sessionCaptures, annotations: annotations, activities: activities);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateSessionHtmlReportAsync(session.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Investigation Alpha", html);
        Assert.Contains("Looks suspicious", html);
        Assert.Contains("started scan", html);
        Assert.Contains("iot.example.com", html);
    }

    [Fact]
    public async Task GenerateSessionHtmlReport_NoActivity_ShowsEmptyState()
    {
        var session = new InvestigationSession { Id = Guid.NewGuid(), Name = "Empty Session" };

        var scopeFactory = BuildScopeFactory(session: session);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateSessionHtmlReportAsync(session.Id, TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("No recorded activity", html);
        Assert.Contains("No annotations recorded", html);
    }

    [Fact]
    public async Task GenerateSessionPdfReport_ReturnsBytesStartingWithPdfMagic()
    {
        var session = new InvestigationSession { Id = Guid.NewGuid(), Name = "PDF Session" };

        var scopeFactory = BuildScopeFactory(session: session);
        var service = new ReportService(scopeFactory, NullLogger<ReportService>.Instance);

        var bytes = await service.GenerateSessionPdfReportAsync(session.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }
}
