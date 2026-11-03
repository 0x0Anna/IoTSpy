using IoTSpy.Proxy.Interception;
using Xunit;

namespace IoTSpy.Proxy.Tests;

public class IptablesHelperTests
{
    [Fact]
    public void BuildRedirectRule_WithoutInterface_MatchesAllInterfaces()
    {
        Assert.Equal(
            "PREROUTING -p tcp --dport 443 -j REDIRECT --to-port 9999",
            IptablesHelper.BuildRedirectRule(443, 9999));
    }

    [Fact]
    public void BuildRedirectRule_WithInterface_AddsInboundInterfaceMatch()
    {
        Assert.Equal(
            "PREROUTING -i eth1 -p tcp --dport 8883 -j REDIRECT --to-port 9999",
            IptablesHelper.BuildRedirectRule(8883, 9999, "eth1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildRedirectRule_BlankInterface_IsTreatedAsNone(string? iface)
    {
        Assert.DoesNotContain("-i ", IptablesHelper.BuildRedirectRule(80, 9999, iface));
    }

    [Theory]
    [InlineData("eth1")]
    [InlineData("eth0")]
    [InlineData("br-lan")]
    [InlineData("wlan0.100")]
    [InlineData("nic_1")]
    [InlineData("abcdefghijklmno")] // 15 characters, the Linux limit
    public void IsValidInterfaceName_AcceptsRealInterfaceNames(string name)
        => Assert.True(IptablesHelper.IsValidInterfaceName(name));

    [Theory]
    [InlineData("")]
    [InlineData("abcdefghijklmnop")]        // 16 characters
    [InlineData("eth0 -j ACCEPT")]          // argument injection
    [InlineData("eth0;reboot")]
    [InlineData("eth0\n-A INPUT")]
    [InlineData("$(id)")]
    public void IsValidInterfaceName_RejectsAnythingThatCouldAlterTheCommand(string name)
        => Assert.False(IptablesHelper.IsValidInterfaceName(name));

    [Fact]
    public void BuildQuicBlockRule_DropsUdp443FromCaptureInterface()
    {
        Assert.Equal(
            "PREROUTING -i ens19 -p udp --dport 443 -j DROP",
            IptablesHelper.BuildQuicBlockRule("ens19"));
    }

    [Fact]
    public void InsertAtTop_AddsPositionAfterChain()
    {
        Assert.Equal(
            "FORWARD 1 -i ens19 -j ACCEPT",
            IptablesHelper.InsertAtTop("FORWARD -i ens19 -j ACCEPT"));
    }
}
