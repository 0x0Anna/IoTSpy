using IoTSpy.Core.Utilities;
using Xunit;

namespace IoTSpy.Core.Tests;

public class ScheduledScanTargetSelectorTests
{
    [Fact]
    public void HasExactlyOneTarget_OnlyDeviceIdSet_ReturnsTrue()
        => Assert.True(ScheduledScanTargetSelector.HasExactlyOneTarget(Guid.NewGuid(), null, null));

    [Fact]
    public void HasExactlyOneTarget_OnlyCidrSet_ReturnsTrue()
        => Assert.True(ScheduledScanTargetSelector.HasExactlyOneTarget(null, "10.0.0.0/24", null));

    [Fact]
    public void HasExactlyOneTarget_OnlyTagSet_ReturnsTrue()
        => Assert.True(ScheduledScanTargetSelector.HasExactlyOneTarget(null, null, "camera"));

    [Fact]
    public void HasExactlyOneTarget_NoneSet_ReturnsFalse()
        => Assert.False(ScheduledScanTargetSelector.HasExactlyOneTarget(null, null, null));

    [Fact]
    public void HasExactlyOneTarget_AllThreeSet_ReturnsFalse()
        => Assert.False(ScheduledScanTargetSelector.HasExactlyOneTarget(Guid.NewGuid(), "10.0.0.0/24", "camera"));

    [Fact]
    public void HasExactlyOneTarget_DeviceIdAndCidrBothSet_ReturnsFalse()
        => Assert.False(ScheduledScanTargetSelector.HasExactlyOneTarget(Guid.NewGuid(), "10.0.0.0/24", null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HasExactlyOneTarget_WhitespaceOnlyCidrAndTag_TreatedAsUnset(string blank)
        => Assert.False(ScheduledScanTargetSelector.HasExactlyOneTarget(null, blank, blank));

    [Fact]
    public void CountTargets_CountsPopulatedSelectorsOnly()
        => Assert.Equal(2, ScheduledScanTargetSelector.CountTargets(Guid.NewGuid(), "10.0.0.0/24", null));
}
