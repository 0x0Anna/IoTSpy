using System.Collections.Concurrent;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoTSpy.Scanner.Tests;

public class ScannerServiceTests
{
    /// <summary>
    /// Overrides the port-scan step with a controllable gate so tests can hold a "scan" open
    /// for as long as needed without depending on real network I/O or PortScanner internals.
    /// </summary>
    private sealed class GatedScannerService(
        IServiceScopeFactory scopeFactory,
        int maxConcurrentScans,
        ConcurrentDictionary<Guid, TaskCompletionSource> gates)
        : IoTSpy.Scanner.ScannerService(
            scopeFactory,
            new PortScanner(NullLogger<PortScanner>.Instance),
            new ServiceFingerprinter(NullLogger<ServiceFingerprinter>.Instance),
            new CredentialTester(NullLogger<CredentialTester>.Instance),
            new CveLookupService(new HttpClient(), NullLogger<CveLookupService>.Instance),
            new ConfigAuditor(NullLogger<ConfigAuditor>.Instance),
            NullLogger<IoTSpy.Scanner.ScannerService>.Instance,
            maxConcurrentScans)
    {
        protected override async Task<List<ScanFinding>> ScanPortsAsync(ScanJob job, CancellationToken ct)
        {
            var gate = gates.GetOrAdd(job.Id, _ => new TaskCompletionSource());
            using var registration = ct.Register(() => gate.TrySetCanceled(ct));
            await gate.Task;
            return [];
        }
    }

    private static (IoTSpy.Scanner.ScannerService Service, ConcurrentDictionary<Guid, ScanJob> Jobs, ConcurrentDictionary<Guid, TaskCompletionSource> Gates)
        BuildService(int maxConcurrentScans)
    {
        var jobs = new ConcurrentDictionary<Guid, ScanJob>();
        var gates = new ConcurrentDictionary<Guid, TaskCompletionSource>();

        var jobRepo = new Mock<IScanJobRepository>();
        jobRepo.Setup(r => r.AddAsync(It.IsAny<ScanJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScanJob j, CancellationToken _) => { jobs[j.Id] = j; return j; });
        jobRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => jobs.TryGetValue(id, out var j) ? j : null);
        jobRepo.Setup(r => r.UpdateAsync(It.IsAny<ScanJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScanJob j, CancellationToken _) => { jobs[j.Id] = j; return j; });
        jobRepo.Setup(r => r.AddFindingsAsync(It.IsAny<IEnumerable<ScanFinding>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        jobRepo.Setup(r => r.GetFindingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var deviceRepo = new Mock<IDeviceRepository>();
        deviceRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);

        var services = new ServiceCollection();
        services.AddSingleton(jobRepo.Object);
        services.AddSingleton(deviceRepo.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var service = new GatedScannerService(scopeFactory, maxConcurrentScans, gates);
        return (service, jobs, gates);
    }

    private static ScanJob MakeJob() => new()
    {
        DeviceId = Guid.NewGuid(),
        TargetIp = "127.0.0.1",
        EnableFingerprinting = false,
        EnableCredentialTest = false,
        EnableCveLookup = false,
        EnableConfigAudit = false
    };

    private static async Task<ScanStatus> WaitForStatusAsync(
        ConcurrentDictionary<Guid, ScanJob> jobs, Guid id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (jobs.TryGetValue(id, out var j) && j.Status != ScanStatus.Pending && j.Status != ScanStatus.Running)
                return j.Status;
            await Task.Delay(20);
        }
        return jobs[id].Status;
    }

    [Fact]
    public async Task StartScanAsync_ReturnsPersistedJobImmediately_WithoutBlockingOnQueueSlot()
    {
        var (service, jobs, gates) = BuildService(maxConcurrentScans: 1);

        // Fill the single slot with a scan that never completes on its own.
        var blocker = MakeJob();
        await service.StartScanAsync(blocker, TestContext.Current.CancellationToken);

        // A second job should still be accepted and persisted immediately, without waiting
        // for the running job to finish.
        var second = MakeJob();
        var task = service.StartScanAsync(second, TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(task, completed);
        Assert.True(jobs.ContainsKey(second.Id));

        await WaitUntilAsync(() => gates.ContainsKey(blocker.Id), TimeSpan.FromSeconds(2));
        gates[blocker.Id].TrySetResult();
    }

    [Fact]
    public async Task JobBeyondConcurrencyCap_StaysPending_ThenIsAdmittedWhenSlotFrees()
    {
        var (service, jobs, gates) = BuildService(maxConcurrentScans: 2);

        var jobA = MakeJob();
        var jobB = MakeJob();
        var jobC = MakeJob();

        await service.StartScanAsync(jobA, TestContext.Current.CancellationToken);
        await service.StartScanAsync(jobB, TestContext.Current.CancellationToken);
        await service.StartScanAsync(jobC, TestContext.Current.CancellationToken);

        // Give the two worker loops time to admit A and B.
        await WaitUntilAsync(() => gates.ContainsKey(jobA.Id) && gates.ContainsKey(jobB.Id), TimeSpan.FromSeconds(2));

        // C should not have been admitted yet (no gate registered for it means ScanPortsAsync
        // hasn't been invoked for it), and its persisted status must still be Pending.
        Assert.False(gates.ContainsKey(jobC.Id));
        Assert.Equal(ScanStatus.Pending, jobs[jobC.Id].Status);
        Assert.True(service.IsScanRunning(jobC.Id));

        // Free one slot — C should now be admitted (Running or already completed).
        gates[jobA.Id].TrySetResult();

        await WaitUntilAsync(() => gates.ContainsKey(jobC.Id), TimeSpan.FromSeconds(2));
        Assert.True(gates.ContainsKey(jobC.Id));

        gates[jobB.Id].TrySetResult();
        gates[jobC.Id].TrySetResult();

        await WaitForStatusAsync(jobs, jobC.Id, TimeSpan.FromSeconds(2));
        Assert.Equal(ScanStatus.Completed, jobs[jobC.Id].Status);
    }

    [Fact]
    public async Task CancelScanAsync_OnQueuedJob_RemovesItWithoutEverExecuting()
    {
        var (service, jobs, gates) = BuildService(maxConcurrentScans: 1);

        var running = MakeJob();
        var queued = MakeJob();

        await service.StartScanAsync(running, TestContext.Current.CancellationToken);
        await service.StartScanAsync(queued, TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => gates.ContainsKey(running.Id), TimeSpan.FromSeconds(2));
        Assert.False(gates.ContainsKey(queued.Id));

        await service.CancelScanAsync(queued.Id);

        Assert.Equal(ScanStatus.Cancelled, jobs[queued.Id].Status);
        Assert.False(service.IsScanRunning(queued.Id));

        // Free the running slot; the cancelled job must never be executed even though a
        // worker loop will dequeue its id.
        gates[running.Id].TrySetResult();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(gates.ContainsKey(queued.Id));
        Assert.Equal(ScanStatus.Cancelled, jobs[queued.Id].Status);
    }

    [Fact]
    public async Task MaxConcurrentScans_GovernsTheAdmissionCap()
    {
        var (service, jobs, gates) = BuildService(maxConcurrentScans: 2);

        var a = MakeJob();
        var b = MakeJob();
        var c = MakeJob();

        await service.StartScanAsync(a, TestContext.Current.CancellationToken);
        await service.StartScanAsync(b, TestContext.Current.CancellationToken);
        await service.StartScanAsync(c, TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => gates.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Equal(2, gates.Count);
        Assert.Equal(1, new[] { a, b, c }.Count(j => jobs[j.Id].Status == ScanStatus.Pending));

        foreach (var gate in gates.Values) gate.TrySetResult();
    }

    [Fact]
    public async Task IsScanRunning_TrueForQueuedAndRunningJobs_FalseAfterCompletion()
    {
        var (service, jobs, gates) = BuildService(maxConcurrentScans: 1);

        var running = MakeJob();
        var queued = MakeJob();

        await service.StartScanAsync(running, TestContext.Current.CancellationToken);
        await service.StartScanAsync(queued, TestContext.Current.CancellationToken);

        Assert.True(service.IsScanRunning(running.Id));
        Assert.True(service.IsScanRunning(queued.Id));

        await WaitUntilAsync(() => gates.ContainsKey(running.Id), TimeSpan.FromSeconds(2));
        gates[running.Id].TrySetResult();
        await WaitForStatusAsync(jobs, running.Id, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => gates.ContainsKey(queued.Id), TimeSpan.FromSeconds(2));

        gates[queued.Id].TrySetResult();
        await WaitForStatusAsync(jobs, queued.Id, TimeSpan.FromSeconds(2));

        Assert.False(service.IsScanRunning(running.Id));
        Assert.False(service.IsScanRunning(queued.Id));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not met within timeout");
    }
}
