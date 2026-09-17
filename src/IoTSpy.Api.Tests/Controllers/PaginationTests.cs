using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class PaginationTests
{
    private static ManipulationController MakeManipController(
        IManipulationRuleRepository? rules = null,
        IBreakpointRepository? breakpoints = null,
        IReplaySessionRepository? replays = null,
        IFuzzerJobRepository? fuzzer = null)
    {
        var rs = replays ?? Substitute.For<IReplaySessionRepository>();
        var fj = fuzzer ?? Substitute.For<IFuzzerJobRepository>();
        if (replays is null)
        {
            rs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ReplaySession>());
            rs.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        }
        if (fuzzer is null)
        {
            fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<FuzzerJob>());
            fj.CountAsync(Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(0);
        }

        return new ManipulationController(
            Substitute.For<IManipulationService>(),
            rules ?? Substitute.For<IManipulationRuleRepository>(),
            Substitute.For<IManipulationRuleCache>(),
            breakpoints ?? Substitute.For<IBreakpointRepository>(),
            rs, fj,
            Substitute.For<ICaptureRepository>(),
            Substitute.For<IApiSpecRepository>(),
            Substitute.For<IAuditRepository>());
    }

    [Fact]
    public async Task ListRules_ReturnsPaginatedEnvelope()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ManipulationRule>
        {
            new() { Name = "R1", Action = ManipulationRuleAction.Drop },
            new() { Name = "R2", Action = ManipulationRuleAction.Drop },
        });

        var controller = MakeManipController(rules);
        var result = await controller.ListRules(1, 100, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":2", json);
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"pages\"", json);
    }

    [Fact]
    public async Task ListBreakpoints_ReturnsPaginatedEnvelope()
    {
        var bps = Substitute.For<IBreakpointRepository>();
        bps.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Breakpoint>
        {
            new() { Name = "BP1", Language = ScriptLanguage.JavaScript, ScriptCode = "" },
        });

        var controller = MakeManipController(breakpoints: bps);
        var result = await controller.ListBreakpoints(1, 100, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        Assert.Contains("\"pages\":1", json);
    }

    [Fact]
    public async Task ListReplays_ReturnsPaginatedEnvelopeWithCount()
    {
        var replaySessions = Substitute.For<IReplaySessionRepository>();
        replaySessions.GetAllAsync(1, 20, Arg.Any<CancellationToken>()).Returns(new List<ReplaySession>
        {
            new() { RequestMethod = "GET", RequestHost = "api.test" },
            new() { RequestMethod = "POST", RequestHost = "api.test" },
        });
        replaySessions.CountAsync(Arg.Any<CancellationToken>()).Returns(42);

        var controller = MakeManipController(replays: replaySessions);
        var result = await controller.ListReplays(1, 20, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":42", json);
        Assert.Contains("\"pages\":3", json);
    }

    [Fact]
    public async Task ListFuzzerJobs_ReturnsPaginatedEnvelopeWithCount()
    {
        var fuzzerJobs = Substitute.For<IFuzzerJobRepository>();
        fuzzerJobs.GetAllAsync(1, 20, Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<FuzzerJob>
        {
            new() { Strategy = FuzzerStrategy.Random, MutationCount = 50 },
        });
        fuzzerJobs.CountAsync(Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(1);

        var controller = MakeManipController(fuzzer: fuzzerJobs);
        var result = await controller.ListFuzzerJobs(1, 20, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":1", json);
        Assert.Contains("\"pages\":1", json);
    }

    [Fact]
    public async Task ListJobs_Scanner_ReturnsPaginatedEnvelope()
    {
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(1, 20, Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(new List<ScanJob>
        {
            new() { TargetIp = "10.0.0.1" },
            new() { TargetIp = "10.0.0.2" },
        });
        scanJobs.CountAsync(Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(55);

        var scopes = Substitute.For<IScanScopeRepository>();
        scopes.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<ScanScope>());
        var controller = new ScannerController(
            Substitute.For<IScannerService>(), scanJobs, Substitute.For<IDeviceRepository>(), scopes, Substitute.For<IAuditRepository>());
        var result = await controller.ListJobs(1, 20, ct: TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":55", json);
        Assert.Contains("\"pages\":3", json);
    }

    [Fact]
    public async Task ListDevices_ReturnsPaginatedEnvelope()
    {
        var devices = Substitute.For<IDeviceRepository>();
        devices.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Device>
        {
            new() { IpAddress = "192.168.1.1" },
            new() { IpAddress = "192.168.1.2" },
            new() { IpAddress = "192.168.1.3" },
        });

        var controller = new DevicesController(devices);
        var result = await controller.List(1, 100, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":3", json);
        Assert.Contains("\"items\"", json);
    }
}
