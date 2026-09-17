using System.Runtime.InteropServices;

namespace IoTSpy.Api.Services;

/// <summary>Thin wrapper over <see cref="RuntimeInformation"/> so OS-dependent controller logic is unit-testable.</summary>
public interface IPlatformInfo
{
    bool IsWindows { get; }
}

public sealed class PlatformInfo : IPlatformInfo
{
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
