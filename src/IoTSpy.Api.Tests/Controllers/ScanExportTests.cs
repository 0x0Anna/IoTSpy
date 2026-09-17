using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class ScanExportTests
{
    private static ScannerController MakeController(IScanJobRepository scanJobs)
    {
        var scopes = Substitute.For<IScanScopeRepository>();
        scopes.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<ScanScope>());
        return new(Substitute.For<IScannerService>(), scanJobs, Substitute.For<IDeviceRepository>(), scopes, Substitute.For<IAuditRepository>());
    }

    [Fact]
    public async Task ExportFindings_WhenJobNotFound_ReturnsNotFound()
    {
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ScanJob?)null);

        var controller = MakeController(scanJobs);
        var result = await controller.ExportFindings(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportFindings_ReturnsJsonWithJobIdAndDeviceId()
    {
        var jobId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var job = new ScanJob { Id = jobId, DeviceId = deviceId, TargetIp = "10.0.0.1", Status = ScanStatus.Completed };
        var findings = new List<ScanFinding>
        {
            new()
            {
                ScanJobId = jobId,
                Type = ScanFindingType.OpenPort,
                Severity = ScanFindingSeverity.Low,
                Title = "Open port 22",
                Port = 22,
                Protocol = "tcp"
            }
        };

        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);
        scanJobs.GetFindingsAsync(jobId, Arg.Any<CancellationToken>()).Returns(findings);

        var controller = MakeController(scanJobs);
        var result = await controller.ExportFindings(jobId, TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal($"scan-{jobId}.json", result.FileDownloadName);

        var json = Encoding.UTF8.GetString(result.FileContents);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(jobId.ToString(), doc.RootElement.GetProperty("jobId").GetString());
        Assert.Equal(deviceId.ToString(), doc.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task ExportFindings_WithNoFindings_ReturnsEmptyArray()
    {
        var jobId = Guid.NewGuid();
        var job = new ScanJob { Id = jobId, TargetIp = "10.0.0.1", Status = ScanStatus.Completed };

        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetByIdAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);
        scanJobs.GetFindingsAsync(jobId, Arg.Any<CancellationToken>()).Returns(new List<ScanFinding>());

        var controller = MakeController(scanJobs);
        var result = await controller.ExportFindings(jobId, TestContext.Current.CancellationToken) as FileContentResult;

        Assert.NotNull(result);
        var json = Encoding.UTF8.GetString(result.FileContents);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("findings").GetArrayLength());
    }
}
