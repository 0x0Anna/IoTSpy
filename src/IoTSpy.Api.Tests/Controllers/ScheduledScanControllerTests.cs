using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class ScheduledScanControllerTests
{
    private static ScheduledScan MakeScan(Guid? deviceId = null) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId ?? Guid.NewGuid(),
        CronExpression = "0 * * * *",
        IsEnabled = true
    };

    [Fact]
    public async Task List_ReturnsAllScheduledScans()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        var scans = new List<ScheduledScan> { MakeScan(), MakeScan() };
        scanRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(scans);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var result = await controller.List(TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(scans, result.Value);
    }

    [Fact]
    public async Task Get_WhenFound_ReturnsOk()
    {
        var scan = MakeScan();
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.GetByIdAsync(scan.Id, Arg.Any<CancellationToken>()).Returns(scan);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var result = await controller.Get(scan.Id, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(scan, result.Value);
    }

    [Fact]
    public async Task Get_WhenNotFound_ReturnsNotFound()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ScheduledScan?)null);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var result = await controller.Get(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Create_WithValidData_ReturnsOk()
    {
        var deviceId = Guid.NewGuid();
        var device = new Device { Id = deviceId, IpAddress = "10.0.0.1" };
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        deviceRepo.GetByIdAsync(deviceId, Arg.Any<CancellationToken>()).Returns(device);
        scanRepo.AddAsync(Arg.Any<ScheduledScan>(), Arg.Any<CancellationToken>())
                .Returns(x => x.ArgAt<ScheduledScan>(0));

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(deviceId, "0 * * * *");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var scan = Assert.IsType<ScheduledScan>(result.Value);
        Assert.Equal(deviceId, scan.DeviceId);
    }

    [Fact]
    public async Task Create_WithInvalidCron_ReturnsBadRequest()
    {
        var deviceId = Guid.NewGuid();
        var device = new Device { Id = deviceId, IpAddress = "10.0.0.1" };
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        deviceRepo.GetByIdAsync(deviceId, Arg.Any<CancellationToken>()).Returns(device);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(deviceId, "not-a-cron");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_WithCidrTarget_ReturnsOk()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.AddAsync(Arg.Any<ScheduledScan>(), Arg.Any<CancellationToken>())
                .Returns(x => x.ArgAt<ScheduledScan>(0));

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(null, "0 * * * *", TargetCidr: "10.0.0.0/24");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var scan = Assert.IsType<ScheduledScan>(result.Value);
        Assert.Null(scan.DeviceId);
        Assert.Equal("10.0.0.0/24", scan.TargetCidr);
        await deviceRepo.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithTagTarget_ReturnsOk()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.AddAsync(Arg.Any<ScheduledScan>(), Arg.Any<CancellationToken>())
                .Returns(x => x.ArgAt<ScheduledScan>(0));

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(null, "0 * * * *", TargetTag: "camera");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var scan = Assert.IsType<ScheduledScan>(result.Value);
        Assert.Equal("camera", scan.TargetTag);
    }

    [Fact]
    public async Task Create_WithNoTargetSelected_ReturnsBadRequest()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(null, "0 * * * *");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        await scanRepo.DidNotReceive().AddAsync(Arg.Any<ScheduledScan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithBothDeviceIdAndCidr_ReturnsBadRequest()
    {
        var deviceId = Guid.NewGuid();
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(deviceId, "0 * * * *", TargetCidr: "10.0.0.0/24");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_WithInvalidCidr_ReturnsBadRequest()
    {
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new CreateScheduledScanDto(null, "0 * * * *", TargetCidr: "not-a-cidr");
        var result = await controller.Create(dto, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_SwitchingFromDeviceToCidr_ClearsDeviceId()
    {
        var scan = MakeScan();
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.GetByIdAsync(scan.Id, Arg.Any<CancellationToken>()).Returns(scan);
        scanRepo.UpdateAsync(Arg.Any<ScheduledScan>(), Arg.Any<CancellationToken>())
                .Returns(x => x.ArgAt<ScheduledScan>(0));

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new UpdateScheduledScanDto(TargetCidr: "10.0.0.0/24");
        var result = await controller.Update(scan.Id, dto, TestContext.Current.CancellationToken) as OkObjectResult;

        Assert.NotNull(result);
        var updated = Assert.IsType<ScheduledScan>(result.Value);
        Assert.Null(updated.DeviceId);
        Assert.Equal("10.0.0.0/24", updated.TargetCidr);
    }

    [Fact]
    public async Task Update_WithMoreThanOneTargetSelector_ReturnsBadRequest()
    {
        var scan = MakeScan();
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.GetByIdAsync(scan.Id, Arg.Any<CancellationToken>()).Returns(scan);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var dto = new UpdateScheduledScanDto(TargetCidr: "10.0.0.0/24", TargetTag: "camera");
        var result = await controller.Update(scan.Id, dto, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Delete_CallsRepoAndReturnsNoContent()
    {
        var scan = MakeScan();
        var scanRepo = Substitute.For<IScheduledScanRepository>();
        var deviceRepo = Substitute.For<IDeviceRepository>();
        scanRepo.DeleteAsync(scan.Id, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var controller = new ScheduledScanController(scanRepo, deviceRepo);
        var result = await controller.Delete(scan.Id, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
        await scanRepo.Received(1).DeleteAsync(scan.Id, Arg.Any<CancellationToken>());
    }
}
