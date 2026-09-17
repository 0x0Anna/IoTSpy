using IoTSpy.Core.Enums;

namespace IoTSpy.Core.Models;

public class ScheduledScan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public string CronExpression { get; set; } = "0 * * * *"; // hourly default
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public Guid? LastScanJobId { get; set; }

    /// <summary>Outcome of the scan started at <see cref="LastRunAt"/> / tracked by <see cref="LastScanJobId"/>.</summary>
    public ScanStatus? LastRunStatus { get; set; }

    /// <summary>Set when <see cref="LastRunStatus"/> is <see cref="ScanStatus.Failed"/>; null otherwise.</summary>
    public string? LastRunError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
