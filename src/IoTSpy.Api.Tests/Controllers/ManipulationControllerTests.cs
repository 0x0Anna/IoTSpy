using System.Security.Claims;
using System.Text.Json;
using IoTSpy.Api.Controllers;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class ManipulationControllerTests
{
    private static ManipulationController MakeController(
        IManipulationRuleRepository? rules = null,
        IBreakpointRepository? breakpoints = null,
        IManipulationService? manipService = null,
        IAuditRepository? audit = null,
        ICaptureRepository? captures = null)
    {
        var r = rules ?? Substitute.For<IManipulationRuleRepository>();
        var bp = breakpoints ?? Substitute.For<IBreakpointRepository>();
        var rs = Substitute.For<IReplaySessionRepository>();
        rs.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ReplaySession>());
        rs.CountAsync(Arg.Any<CancellationToken>()).Returns(0);
        var fj = Substitute.For<IFuzzerJobRepository>();
        fj.GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<FuzzerJob>());
        fj.CountAsync(Arg.Any<FuzzerJobStatus?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(0);

        var controller = new ManipulationController(
            manipService ?? Substitute.For<IManipulationService>(),
            r, Substitute.For<IManipulationRuleCache>(), bp, rs, fj,
            captures ?? Substitute.For<ICaptureRepository>(),
            Substitute.For<IApiSpecRepository>(),
            audit ?? Substitute.For<IAuditRepository>());

        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        return controller;
    }

    private static ManipulationRule MakeRule(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Test Rule",
        Action = ManipulationRuleAction.Drop,
        Enabled = true
    };

    private static Breakpoint MakeBreakpoint(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Test BP",
        Language = ScriptLanguage.CSharp,
        ScriptCode = "// noop"
    };

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRules_ReturnsPaginatedEnvelope()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ManipulationRule> { MakeRule(), MakeRule(), MakeRule() });

        var result = await MakeController(rules).ListRules(1, 10, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":3", json);
        Assert.Contains("\"items\"", json);
        Assert.Contains("\"page\":1", json);
    }

    [Fact]
    public async Task ListRules_PageSizeClamped()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ManipulationRule>());

        var result = await MakeController(rules).ListRules(1, 9999, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"pageSize\":500", json);
    }

    [Fact]
    public async Task GetRule_WhenFound_ReturnsRule()
    {
        var id = Guid.NewGuid();
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeRule(id));

        var result = await MakeController(rules).GetRule(id) as OkObjectResult;

        Assert.NotNull(result);
        Assert.IsType<ManipulationRule>(result.Value);
    }

    [Fact]
    public async Task GetRule_WhenNotFound_Returns404()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ManipulationRule?)null);

        var result = await MakeController(rules).GetRule(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CreateRule_ValidDto_Returns201()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.AddAsync(Arg.Any<ManipulationRule>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<ManipulationRule>());

        var dto = new CreateRuleDto("Drop ads", ManipulationRuleAction.Drop, HostPattern: "ads\\.example\\.com");
        var result = await MakeController(rules).CreateRule(dto) as CreatedAtActionResult;

        Assert.NotNull(result);
        await rules.Received(1).AddAsync(
            Arg.Is<ManipulationRule>(r => r.Name == "Drop ads"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRule_WhenFound_UpdatesAndReturnsRule()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id);
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(rule);
        rules.UpdateAsync(Arg.Any<ManipulationRule>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<ManipulationRule>());
        var audit = Substitute.For<IAuditRepository>();

        var dto = new UpdateRuleDto(Name: "Renamed");
        var result = await MakeController(rules, audit: audit).UpdateRule(id, dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal("Renamed", rule.Name);
        await rules.Received(1).UpdateAsync(Arg.Any<ManipulationRule>(), Arg.Any<CancellationToken>());
        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "Update" && e.EntityType == "ManipulationRule"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRule_WhenNotFound_Returns404()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ManipulationRule?)null);

        var result = await MakeController(rules).UpdateRule(Guid.NewGuid(), new UpdateRuleDto(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteRule_CallsDeleteAndReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeRule(id));
        var audit = Substitute.For<IAuditRepository>();

        var result = await MakeController(rules, audit: audit).DeleteRule(id, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await rules.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "Delete" && e.EntityType == "ManipulationRule"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRule_WhenNotFound_StillReturnsNoContent()
    {
        var id = Guid.NewGuid();
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((ManipulationRule?)null);

        var result = await MakeController(rules).DeleteRule(id, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await rules.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    // ── Bulk delete rules ─────────────────────────────────────────────────────

    [Fact]
    public async Task BulkDeleteRules_ByIds_DeletesAndReturnsCount()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var rules = Substitute.For<IManipulationRuleRepository>();

        var dto = new BulkDeleteRulesDto([id1, id2]);
        var result = await MakeController(rules).BulkDeleteRules(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"deleted\":2", json);
        await rules.Received(1).DeleteManyAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(id1) && ids.Contains(id2)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteRules_All_DeletesAllAndReturnsCount()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();
        rules.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ManipulationRule> { MakeRule(), MakeRule() });

        var dto = new BulkDeleteRulesDto(All: true);
        var result = await MakeController(rules).BulkDeleteRules(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"deleted\":2", json);
        await rules.Received(1).DeleteManyAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeleteRules_EmptyRequest_ReturnsZero()
    {
        var rules = Substitute.For<IManipulationRuleRepository>();

        var dto = new BulkDeleteRulesDto();
        var result = await MakeController(rules).BulkDeleteRules(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"deleted\":0", json);
        await rules.DidNotReceive().DeleteManyAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    // ── Breakpoints ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListBreakpoints_ReturnsPaginatedEnvelope()
    {
        var breakpoints = Substitute.For<IBreakpointRepository>();
        breakpoints.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Breakpoint> { MakeBreakpoint(), MakeBreakpoint() });

        var result = await MakeController(breakpoints: breakpoints)
            .ListBreakpoints(1, 50, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"total\":2", json);
    }

    [Fact]
    public async Task GetBreakpoint_WhenFound_Returns200()
    {
        var id = Guid.NewGuid();
        var breakpoints = Substitute.For<IBreakpointRepository>();
        breakpoints.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeBreakpoint(id));

        var result = await MakeController(breakpoints: breakpoints).GetBreakpoint(id) as OkObjectResult;

        Assert.NotNull(result);
        Assert.IsType<Breakpoint>(result.Value);
    }

    [Fact]
    public async Task GetBreakpoint_WhenNotFound_Returns404()
    {
        var breakpoints = Substitute.For<IBreakpointRepository>();
        breakpoints.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Breakpoint?)null);

        var result = await MakeController(breakpoints: breakpoints).GetBreakpoint(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CreateBreakpoint_AsAdmin_Returns201()
    {
        var breakpoints = Substitute.For<IBreakpointRepository>();
        breakpoints.AddAsync(Arg.Any<Breakpoint>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Breakpoint>());

        var dto = new CreateBreakpointDto("My BP", ScriptLanguage.JavaScript, "console.log('hit');");
        var result = await MakeController(breakpoints: breakpoints).CreateBreakpoint(dto) as CreatedAtActionResult;

        Assert.NotNull(result);
        await breakpoints.Received(1).AddAsync(
            Arg.Is<Breakpoint>(b => b.Name == "My BP"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBreakpoint_CallsDeleteAndAudits()
    {
        var id = Guid.NewGuid();
        var breakpoints = Substitute.For<IBreakpointRepository>();
        breakpoints.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(MakeBreakpoint(id));
        var audit = Substitute.For<IAuditRepository>();

        var result = await MakeController(breakpoints: breakpoints, audit: audit)
            .DeleteBreakpoint(id, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await breakpoints.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "Delete" && e.EntityType == "Breakpoint"),
            Arg.Any<CancellationToken>());
    }

    // ── Fuzzer ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartFuzzer_WhenCaptureNotFound_Returns404()
    {
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CapturedRequest?)null);

        var dto = new StartFuzzerDto(Guid.NewGuid());
        var result = await MakeController(captures: captures).StartFuzzer(dto, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetFuzzerStatus_WhenNotFound_Returns404()
    {
        var controller = MakeController();
        var result = await controller.GetFuzzerStatus(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    // ── TLS bypass audit ──────────────────────────────────────────────────────

    [Fact]
    public async Task StartReplay_WithBypassTlsValidation_SetsSessionFlagAndAudits()
    {
        var captureId = Guid.NewGuid();
        var capture = new CapturedRequest { Id = captureId, Method = "GET", Host = "device.local", Scheme = "https", Port = 443, Path = "/" };
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(capture);
        var manip = Substitute.For<IManipulationService>();
        manip.ReplayAsync(Arg.Any<ReplaySession>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<ReplaySession>());
        var audit = Substitute.For<IAuditRepository>();

        var dto = new StartReplayDto(captureId, BypassTlsValidation: true);
        var result = await MakeController(manipService: manip, captures: captures, audit: audit)
            .StartReplay(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        await manip.Received(1).ReplayAsync(
            Arg.Is<ReplaySession>(s => s.BypassTlsValidation),
            Arg.Any<CancellationToken>());
        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "ReplayBypassTls" && e.EntityType == "ReplaySession"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartReplay_WithoutBypassTlsValidation_DoesNotAuditBypass()
    {
        var captureId = Guid.NewGuid();
        var capture = new CapturedRequest { Id = captureId, Method = "GET", Host = "device.local", Scheme = "https", Port = 443, Path = "/" };
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(capture);
        var manip = Substitute.For<IManipulationService>();
        manip.ReplayAsync(Arg.Any<ReplaySession>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<ReplaySession>());
        var audit = Substitute.For<IAuditRepository>();

        var dto = new StartReplayDto(captureId);
        var result = await MakeController(manipService: manip, captures: captures, audit: audit)
            .StartReplay(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        await manip.Received(1).ReplayAsync(
            Arg.Is<ReplaySession>(s => !s.BypassTlsValidation),
            Arg.Any<CancellationToken>());
        await audit.DidNotReceive().AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "ReplayBypassTls"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartReplay_WithHostPortPathQueryOverrides_AppliesAllOverrides()
    {
        var captureId = Guid.NewGuid();
        var capture = new CapturedRequest
        {
            Id = captureId, Method = "GET", Host = "original.example.com",
            Scheme = "https", Port = 443, Path = "/original", Query = "?a=1"
        };
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(capture);
        var manip = Substitute.For<IManipulationService>();
        manip.ReplayAsync(Arg.Any<ReplaySession>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<ReplaySession>());

        var dto = new StartReplayDto(captureId, Host: "override.example.com", Port: 8443, Path: "/override", Query: "?b=2");
        var result = await MakeController(manipService: manip, captures: captures)
            .StartReplay(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        await manip.Received(1).ReplayAsync(
            Arg.Is<ReplaySession>(s =>
                s.RequestHost == "override.example.com" &&
                s.RequestPort == 8443 &&
                s.RequestPath == "/override" &&
                s.RequestQuery == "?b=2" &&
                s.RequestMethod == "GET"), // omitted override falls back to the original capture's method
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartFuzzer_WithBypassTlsValidation_SetsJobFlagAndAudits()
    {
        var captureId = Guid.NewGuid();
        var capture = new CapturedRequest { Id = captureId, Method = "POST", Host = "device.local", Scheme = "https", Port = 443, Path = "/api" };
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(capture);
        var manip = Substitute.For<IManipulationService>();
        manip.StartFuzzerAsync(Arg.Any<FuzzerJob>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<FuzzerJob>());
        var audit = Substitute.For<IAuditRepository>();

        var dto = new StartFuzzerDto(captureId, BypassTlsValidation: true);
        var result = await MakeController(manipService: manip, captures: captures, audit: audit)
            .StartFuzzer(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        await manip.Received(1).StartFuzzerAsync(
            Arg.Is<FuzzerJob>(j => j.BypassTlsValidation),
            Arg.Any<CancellationToken>());
        await audit.Received(1).AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "FuzzerBypassTls" && e.EntityType == "FuzzerJob"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartFuzzer_WithoutBypassTlsValidation_DoesNotAuditBypass()
    {
        var captureId = Guid.NewGuid();
        var capture = new CapturedRequest { Id = captureId, Method = "POST", Host = "device.local", Scheme = "https", Port = 443, Path = "/api" };
        var captures = Substitute.For<ICaptureRepository>();
        captures.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(capture);
        var manip = Substitute.For<IManipulationService>();
        manip.StartFuzzerAsync(Arg.Any<FuzzerJob>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<FuzzerJob>());
        var audit = Substitute.For<IAuditRepository>();

        var dto = new StartFuzzerDto(captureId);
        var result = await MakeController(manipService: manip, captures: captures, audit: audit)
            .StartFuzzer(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        await manip.Received(1).StartFuzzerAsync(
            Arg.Is<FuzzerJob>(j => !j.BypassTlsValidation),
            Arg.Any<CancellationToken>());
        await audit.DidNotReceive().AddAsync(
            Arg.Is<AuditEntry>(e => e.Action == "FuzzerBypassTls"),
            Arg.Any<CancellationToken>());
    }

    // ── AI Mock ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAiMock_WhenServiceNotConfigured_ReturnsBadRequest()
    {
        var controller = MakeController();
        var result = await controller.GenerateAiMock(new AiMockGenerateDto("example.com", "GET", "/api/test"));
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
