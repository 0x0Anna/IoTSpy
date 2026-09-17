namespace IoTSpy.Core.Utilities;

/// <summary>
/// A <see cref="Models.ScheduledScan"/> needs exactly one target-selection mode:
/// a single device, a CIDR-based device list, or a tag-based device list.
/// </summary>
public static class ScheduledScanTargetSelector
{
    /// <summary>Counts how many of the three target-selector fields are populated.</summary>
    public static int CountTargets(Guid? deviceId, string? targetCidr, string? targetTag) =>
        (deviceId.HasValue ? 1 : 0) +
        (!string.IsNullOrWhiteSpace(targetCidr) ? 1 : 0) +
        (!string.IsNullOrWhiteSpace(targetTag) ? 1 : 0);

    /// <summary>True when exactly one of the three target-selector fields is populated.</summary>
    public static bool HasExactlyOneTarget(Guid? deviceId, string? targetCidr, string? targetTag) =>
        CountTargets(deviceId, targetCidr, targetTag) == 1;
}
