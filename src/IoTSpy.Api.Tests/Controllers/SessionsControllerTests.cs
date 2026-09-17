using System.Security.Claims;
using IoTSpy.Api.Controllers;
using IoTSpy.Api.Hubs;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class SessionsControllerTests
{
    private static (
        SessionsController controller,
        IInvestigationSessionRepository sessionRepo,
        ICaptureAnnotationRepository annotationRepo,
        ISessionActivityRepository activityRepo,
        IAuditRepository auditRepo)
        CreateController(UserRole role = UserRole.Admin)
    {
        var userId = Guid.NewGuid();
        var sessionRepo = Substitute.For<IInvestigationSessionRepository>();
        var annotationRepo = Substitute.For<ICaptureAnnotationRepository>();
        var activityRepo = Substitute.For<ISessionActivityRepository>();
        var auditRepo = Substitute.For<IAuditRepository>();

        var hubContext = Substitute.For<IHubContext<CollaborationHub>>();
        var hubClients = Substitute.For<IHubClients>();
        var clientProxy = Substitute.For<IClientProxy>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Group(Arg.Any<string>()).Returns(clientProxy);
        var publisher = new CollaborationPublisher(hubContext);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "testuser"),
            new Claim(ClaimTypes.Role, role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        var controller = new SessionsController(sessionRepo, annotationRepo, activityRepo, auditRepo, publisher)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (controller, sessionRepo, annotationRepo, activityRepo, auditRepo);
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsActiveSessions()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        sessionRepo.GetAllAsync(false, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvestigationSession>
            {
                new() { Name = "Alpha", IsActive = true },
                new() { Name = "Beta", IsActive = true }
            });

        var result = await controller.GetAll() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Alpha", json);
        Assert.Contains("Beta", json);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ExistingSession_Returns200()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        var session = new InvestigationSession { Name = "Recon", IsActive = true };
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await controller.GetById(session.Id) as OkObjectResult;

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetById_MissingSession_Returns404()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        sessionRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((InvestigationSession?)null);

        var result = await controller.GetById(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AsAdmin_Returns201()
    {
        var (controller, sessionRepo, _, activityRepo, _) = CreateController(UserRole.Admin);
        sessionRepo.CreateAsync(Arg.Any<InvestigationSession>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<InvestigationSession>());

        var result = await controller.Create(new SessionsController.CreateSessionRequest("MySession", null))
            as CreatedAtActionResult;

        Assert.NotNull(result);
        await sessionRepo.Received(1).CreateAsync(
            Arg.Is<InvestigationSession>(s => s.Name == "MySession"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_AsViewer_ReturnsForbid()
    {
        var (controller, _, _, _, _) = CreateController(UserRole.Viewer);

        var result = await controller.Create(new SessionsController.CreateSessionRequest("X", null));

        Assert.IsType<ForbidResult>(result);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingSession_UpdatesName()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        var session = new InvestigationSession { Name = "Old", IsActive = true };
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await controller.Update(session.Id, new SessionsController.UpdateSessionRequest("New", null))
            as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal("New", session.Name);
    }

    // ── AddCapture ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddCapture_NotInSession_AddsAndReturns200()
    {
        var (controller, sessionRepo, _, activityRepo, _) = CreateController(UserRole.Operator);
        var session = new InvestigationSession { IsActive = true };
        var captureId = Guid.NewGuid();
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        sessionRepo.ContainsCaptureAsync(session.Id, captureId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await controller.AddCapture(session.Id, new SessionsController.AddCaptureRequest(captureId))
            as OkObjectResult;

        Assert.NotNull(result);
        await sessionRepo.Received(1).AddCaptureAsync(
            Arg.Is<SessionCapture>(sc => sc.CaptureId == captureId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddCapture_AlreadyInSession_Returns409()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        var session = new InvestigationSession { IsActive = true };
        var captureId = Guid.NewGuid();
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        sessionRepo.ContainsCaptureAsync(session.Id, captureId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await controller.AddCapture(session.Id, new SessionsController.AddCaptureRequest(captureId));

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── GetAnnotations ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAnnotations_ReturnsAnnotationsForSession()
    {
        var (controller, _, annotationRepo, _, _) = CreateController();
        var sessionId = Guid.NewGuid();
        annotationRepo.GetBySessionAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new List<CaptureAnnotation>
            {
                new() { SessionId = sessionId, Note = "Suspicious token" }
            });

        var result = await controller.GetAnnotations(sessionId) as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("Suspicious token", json);
    }

    // ── AddAnnotation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAnnotation_AsViewer_ReturnsForbid()
    {
        var (controller, sessionRepo, _, _, _) = CreateController(UserRole.Viewer);
        sessionRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new InvestigationSession { IsActive = true });

        var result = await controller.AddAnnotation(Guid.NewGuid(),
            new SessionsController.AddAnnotationRequest(Guid.NewGuid(), "note", null));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task AddAnnotation_AsOperator_Returns201()
    {
        var (controller, sessionRepo, annotationRepo, _, _) = CreateController(UserRole.Operator);
        var session = new InvestigationSession { IsActive = true };
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        annotationRepo.AddAsync(Arg.Any<CaptureAnnotation>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<CaptureAnnotation>());

        var result = await controller.AddAnnotation(session.Id,
            new SessionsController.AddAnnotationRequest(Guid.NewGuid(), "PII leak", "pii,auth"));

        Assert.IsType<CreatedAtActionResult>(result);
        await annotationRepo.Received(1).AddAsync(
            Arg.Is<CaptureAnnotation>(a => a.Note == "PII leak" && a.Tags == "pii,auth"),
            Arg.Any<CancellationToken>());
    }

    // ── GenerateShareToken ────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateShareToken_ReturnsTokenAndUrl()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        var session = new InvestigationSession { IsActive = true };
        sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("iotspy.local");

        var result = await controller.GenerateShareToken(session.Id) as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("token", json);
        Assert.Contains("url", json);
        Assert.NotNull(session.ShareToken);
        Assert.Equal(64, session.ShareToken!.Length); // 32 bytes as hex = 64 chars
    }

    // ── GetByShareToken ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetByShareToken_ValidToken_ReturnsSessionPayload()
    {
        var (controller, sessionRepo, annotationRepo, _, _) = CreateController();
        var session = new InvestigationSession { Name = "SharedSession", ShareToken = "abc", IsActive = true };
        sessionRepo.GetByShareTokenAsync("abc", Arg.Any<CancellationToken>()).Returns(session);
        sessionRepo.GetSessionCapturesAsync(session.Id, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new List<SessionCapture>());
        annotationRepo.GetBySessionAsync(session.Id, Arg.Any<CancellationToken>()).Returns(new List<CaptureAnnotation>());

        var result = await controller.GetByShareToken("abc") as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("SharedSession", json);
        Assert.Contains("iotspy-session/v1", json);
    }

    [Fact]
    public async Task GetByShareToken_InvalidToken_Returns404()
    {
        var (controller, sessionRepo, _, _, _) = CreateController();
        sessionRepo.GetByShareTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((InvestigationSession?)null);

        var result = await controller.GetByShareToken("bad-token");

        Assert.IsType<NotFoundResult>(result);
    }
}
