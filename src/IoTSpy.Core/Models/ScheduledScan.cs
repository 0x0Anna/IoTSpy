using IoTSpy.Core.Enums;

namespace IoTSpy.Core.Models;

public class ScheduledScan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Single-device target. Exactly one of <see cref="DeviceId"/>, <see cref="TargetCidr"/>,
    /// <see cref="TargetTag"/> must be set — see <see cref="Utilities.ScheduledScanTargetSelector"/>.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>CIDR-based target list (e.g. "10.0.0.0/24") — resolved against all known devices at fire time.</summary>
    public string? TargetCidr { get; set; }

    /// <summary>Tag-based target list — matches devices whose comma-separated <see cref="Device.Tags"/>
    /// contains this tag (case-insensitive, same convention as <see cref="CaptureAnnotation.Tags"/>).</summary>
    public string? TargetTag { get; set; }

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
