using System.Security.Claims;
using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class DashboardControllerTests
{
    private static (DashboardController controller, IDashboardLayoutRepository repo, Guid userId) MakeController()
    {
        var userId = Guid.NewGuid();
        var repo = Substitute.For<IDashboardLayoutRepository>();

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        var controller = new DashboardController(repo)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

        return (controller, repo, userId);
    }

    private static DashboardLayout MakeLayout(Guid userId, string name = "My Layout") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        LayoutJson = "{}",
        FiltersJson = "{}"
    };

    // ── GetLayouts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLayouts_ReturnsOnlyCurrentUsersLayouts()
    {
        var (controller, repo, userId) = MakeController();
        var layouts = new List<DashboardLayout> { MakeLayout(userId), MakeLayout(userId) };
        repo.GetByUserAsync(userId, Arg.Any<CancellationToken>()).Returns(layouts);

        var result = await controller.GetLayouts() as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(layouts, result.Value);
        await repo.Received(1).GetByUserAsync(userId, Arg.Any<CancellationToken>());
    }

    // ── CreateLayout ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLayout_SetsUserIdFromAuthenticatedUser()
    {
        var (controller, repo, userId) = MakeController();
        var req = new DashboardController.CreateLayoutRequest("New Layout", "{\"a\":1}", "{\"b\":2}");

        var result = await controller.CreateLayout(req) as CreatedResult;

        Assert.NotNull(result);
        var created = Assert.IsType<DashboardLayout>(result.Value);
        Assert.Equal(userId, created.UserId);
        Assert.Equal("New Layout", created.Name);
        Assert.Equal("{\"a\":1}", created.LayoutJson);
        Assert.Equal("{\"b\":2}", created.FiltersJson);
        await repo.Received(1).CreateAsync(Arg.Is<DashboardLayout>(l => l.UserId == userId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLayout_WithNullJsonFields_DefaultsToEmptyObject()
    {
        var (controller, _, _) = MakeController();
        var req = new DashboardController.CreateLayoutRequest("New Layout", null, null);

        var result = await controller.CreateLayout(req) as CreatedResult;

        Assert.NotNull(result);
        var created = Assert.IsType<DashboardLayout>(result.Value);
        Assert.Equal("{}", created.LayoutJson);
        Assert.Equal("{}", created.FiltersJson);
    }

    // ── UpdateLayout ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLayout_WhenNotFound_ReturnsNotFound()
    {
        var (controller, repo, _) = MakeController();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DashboardLayout?)null);

        var req = new DashboardController.UpdateLayoutRequest("Renamed", null, null, null);
        var result = await controller.UpdateLayout(Guid.NewGuid(), req);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateLayout_WhenOwnedByAnotherUser_ReturnsForbid()
    {
        var (controller, repo, _) = MakeController();
        var otherUsersLayout = MakeLayout(Guid.NewGuid());
        repo.GetByIdAsync(otherUsersLayout.Id, Arg.Any<CancellationToken>()).Returns(otherUsersLayout);

        var req = new DashboardController.UpdateLayoutRequest("Renamed", null, null, null);
        var result = await controller.UpdateLayout(otherUsersLayout.Id, req);

        Assert.IsType<ForbidResult>(result);
        await repo.DidNotReceive().UpdateAsync(Arg.Any<DashboardLayout>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateLayout_AppliesOnlyProvidedFields()
    {
        var (controller, repo, userId) = MakeController();
        var existing = MakeLayout(userId, "Original Name");
        existing.LayoutJson = "{\"original\":true}";
        existing.FiltersJson = "{\"filter\":1}";
        existing.IsDefault = false;
        repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        repo.UpdateAsync(Arg.Any<DashboardLayout>(), Arg.Any<CancellationToken>())
            .Returns(x => x.ArgAt<DashboardLayout>(0));

        // Only Name is provided; LayoutJson/FiltersJson/IsDefault are null and must not overwrite.
        var req = new DashboardController.UpdateLayoutRequest("Renamed", null, null, null);
        var result = await controller.UpdateLayout(existing.Id, req) as OkObjectResult;

        Assert.NotNull(result);
        var updated = Assert.IsType<DashboardLayout>(result.Value);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("{\"original\":true}", updated.LayoutJson);
        Assert.Equal("{\"filter\":1}", updated.FiltersJson);
        Assert.False(updated.IsDefault);
    }

    [Fact]
    public async Task UpdateLayout_WithAllFieldsProvided_OverwritesAll()
    {
        var (controller, repo, userId) = MakeController();
        var existing = MakeLayout(userId, "Original Name");
        repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        repo.UpdateAsync(Arg.Any<DashboardLayout>(), Arg.Any<CancellationToken>())
            .Returns(x => x.ArgAt<DashboardLayout>(0));

        var req = new DashboardController.UpdateLayoutRequest("Renamed", "{\"x\":1}", "{\"y\":2}", true);
        var result = await controller.UpdateLayout(existing.Id, req) as OkObjectResult;

        Assert.NotNull(result);
        var updated = Assert.IsType<DashboardLayout>(result.Value);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("{\"x\":1}", updated.LayoutJson);
        Assert.Equal("{\"y\":2}", updated.FiltersJson);
        Assert.True(updated.IsDefault);
    }

    // ── DeleteLayout ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteLayout_WhenNotFound_ReturnsNotFound()
    {
        var (controller, repo, _) = MakeController();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DashboardLayout?)null);

        var result = await controller.DeleteLayout(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteLayout_WhenOwnedByAnotherUser_ReturnsForbid()
    {
        var (controller, repo, _) = MakeController();
        var otherUsersLayout = MakeLayout(Guid.NewGuid());
        repo.GetByIdAsync(otherUsersLayout.Id, Arg.Any<CancellationToken>()).Returns(otherUsersLayout);

        var result = await controller.DeleteLayout(otherUsersLayout.Id);

        Assert.IsType<ForbidResult>(result);
        await repo.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLayout_WhenOwnedByCurrentUser_ReturnsNoContent()
    {
        var (controller, repo, userId) = MakeController();
        var layout = MakeLayout(userId);
        repo.GetByIdAsync(layout.Id, Arg.Any<CancellationToken>()).Returns(layout);

        var result = await controller.DeleteLayout(layout.Id);

        Assert.IsType<NoContentResult>(result);
        await repo.Received(1).DeleteAsync(layout.Id, Arg.Any<CancellationToken>());
    }
}
