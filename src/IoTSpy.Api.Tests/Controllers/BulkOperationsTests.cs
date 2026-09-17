using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class BulkOperationsTests
{
    // ── Bulk rule enable/disable ──────────────────────────────────────────────

    private static ManipulationController MakeManipController(
        IManipulationRuleRepository? rules = null,
        IBreakpointRepository? breakpoints = null,
        IReplaySessionRepository? replays = null,
        IFuzzerJobRepository? fuzzer = null,
        IApiSpecRepository? apiSpecs = null)
    {
        var r = rules ?? Substitute.For<IManipulationRuleRepository>();
        var bp = breakpoints ?? Substitute.For<IBreakpointRepository>();
        var rs = replays ?? Substitute.For<IReplaySessionRepository>();
        var fj = fuzzer ?? Substitute.For<IFuzzerJobRepository>();
        var api = apiSpecs ?? Substitute.For<IApiSpecRepository>();

        rs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ReplaySession>());
        rs.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<FuzzerJob>());
        fj.CountAsync(Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(0);

        return new ManipulationController(
            Substitute.For<IManipulationService>(), r, Substitute.For<IManipulationRuleCache>(), bp, rs, fj,
            Substitute.For<ICaptureRepository>(), api, Substitute.For<IAuditRepository>());
    }

    [Fact]
    public async Task BulkUpdateRules_EnablesSpecifiedRules()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var rule1 = new ManipulationRule { Id = id1, Name = "R1", Enabled = false, Action = ManipulationRuleAction.Drop };
        var rule2 = new ManipulationRule { Id = id2, Name = "R2", Enabled = false, Action = ManipulationRuleAction.Drop };

        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(id1, Arg.Any<CancellationToken>()).Returns(rule1);
        rules.GetByIdAsync(id2, Arg.Any<CancellationToken>()).Returns(rule2);
        rules.UpdateAsync(Arg.Any<ManipulationRule>(), Arg.Any<CancellationToken>()).Returns(ci => ci.ArgAt<ManipulationRule>(0));

        var controller = MakeManipController(rules);
        var dto = new BulkUpdateRulesDto([id1, id2], Enabled: true);
        var result = await controller.BulkUpdateRules(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"updated\":2", json);
        await rules.Received(2).UpdateAsync(Arg.Any<ManipulationRule>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUpdateRules_SkipsMissingIds()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ManipulationRule?)null);

        var controller = MakeManipController(rules);
        var dto = new BulkUpdateRulesDto([Guid.NewGuid(), Guid.NewGuid()], Enabled: false);
        var result = await controller.BulkUpdateRules(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"updated\":0", json);
    }

    // ── Cancel-all scans ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAllScans_CancelsRunningScans()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var jobs = new List<ScanJob>
        {
            new() { Id = id1, TargetIp = "10.0.0.1" },
            new() { Id = id2, TargetIp = "10.0.0.2" },
            new() { Id = id3, TargetIp = "10.0.0.3" },
        };

        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(1, 1000, Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(jobs);
        scanJobs.CountAsync(Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(3);

        var scanner = Substitute.For<IScannerService>();
        scanner.IsScanRunning(id1).Returns(true);
        scanner.IsScanRunning(id2).Returns(false);
        scanner.IsScanRunning(id3).Returns(true);

        var scopes = Substitute.For<IScanScopeRepository>();
        scopes.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<ScanScope>());
        var controller = new ScannerController(scanner, scanJobs, Substitute.For<IDeviceRepository>(), scopes, Substitute.For<IAuditRepository>());
        var result = await controller.CancelAllScans(TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"cancelled\":2", json);
        await scanner.Received(1).CancelScanAsync(id1);
        await scanner.DidNotReceive().CancelScanAsync(id2);
        await scanner.Received(1).CancelScanAsync(id3);
    }

    [Fact]
    public async Task CancelAllScans_WhenNoneRunning_ReturnsZero()
    {
        var scanJobs = Substitute.For<IScanJobRepository>();
        scanJobs.GetAllAsync(1, 1000, Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(new List<ScanJob>());
        scanJobs.CountAsync(Arg.Any<ScanStatus?>(), Arg.Any<Guid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(0);

        var scanner = Substitute.For<IScannerService>();
        var scopes = Substitute.For<IScanScopeRepository>();
        scopes.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<ScanScope>());
        var controller = new ScannerController(scanner, scanJobs, Substitute.For<IDeviceRepository>(), scopes, Substitute.For<IAuditRepository>());
        var result = await controller.CancelAllScans(TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"cancelled\":0", json);
    }

    // ── Bulk capture delete by filter ─────────────────────────────────────────

    [Fact]
    public async Task Clear_WithFilterParams_CallsClearByFilter()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.ClearByFilterAsync(Arg.Any<CaptureFilter>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var controller = new CapturesController(repo);
        var result = await controller.Clear(
            null, "api.example.com", "GET", null, null, null, null, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await repo.Received(1).ClearByFilterAsync(
            Arg.Is<CaptureFilter>(f => f.HostContains == "api.example.com" && f.Method == "GET"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_WithDateRangeFilter_CallsClearByFilter()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;
        var repo = Substitute.For<ICaptureRepository>();
        repo.ClearByFilterAsync(Arg.Any<CaptureFilter>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var controller = new CapturesController(repo);
        var result = await controller.Clear(null, null, null, null, from, to, null, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await repo.Received(1).ClearByFilterAsync(
            Arg.Is<CaptureFilter>(f => f.From == from && f.To == to),
            Arg.Any<CancellationToken>());
    }
}
