using IoTSpy.Core.Models;
using IoTSpy.Proxy.Interception;
using Xunit;

namespace IoTSpy.Proxy.Tests;

public class PassthroughCaptureCoalescerTests
{
    private const string DeviceIp = "192.0.2.204";
    private const string Telemetry = "telemetry.example.net";

    [Fact]
    public void RepeatConnections_InWindow_ShareOneGroup()
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());

        var (first, firstIsNew) = c.Join(DeviceIp, Telemetry, 443);
        var (second, secondIsNew) = c.Join(DeviceIp, Telemetry.ToUpperInvariant() + ".", 443);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.Same(first, second);
        Assert.Equal(2, first.Connections);
    }

    [Theory]
    [InlineData("192.0.2.111", Telemetry, 443)]
    [InlineData(DeviceIp, "storage.example.com", 443)]
    [InlineData(DeviceIp, Telemetry, 8443)]
    public void DifferentClientHostOrPort_GetsOwnGroup(string client, string host, int port)
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());
        c.Join(DeviceIp, Telemetry, 443);

        Assert.True(c.Join(client, host, port).IsFirst);
    }

    [Fact]
    public void AfterWindow_StartsNewGroup()
    {
        var time = new FakeTimeProvider();
        var c = new PassthroughCaptureCoalescer(time);
        var (first, _) = c.Join(DeviceIp, Telemetry, 443);

        time.Advance(PassthroughCaptureCoalescer.Window);
        var (next, isNew) = c.Join(DeviceIp, Telemetry, 443);

        Assert.True(isNew);
        Assert.NotSame(first, next);
    }

    [Fact]
    public void AbandonedGroup_IsReplacedByNextConnection()
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());
        var (first, _) = c.Join(DeviceIp, Telemetry, 443);
        first.Abandon(); // e.g. upstream unreachable, no row recorded

        Assert.True(c.Join(DeviceIp, Telemetry, 443).IsFirst);
    }

    [Fact]
    public void Abandon_AfterRecording_KeepsGroup()
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());
        var (first, _) = c.Join(DeviceIp, Telemetry, 443);
        first.Recorded(Guid.NewGuid(), "passthrough: x", new TlsMetadata());
        first.Abandon();

        Assert.False(c.Join(DeviceIp, Telemetry, 443).IsFirst);
    }

    [Fact]
    public void Complete_SumsBytesAndSpansDuration()
    {
        var time = new FakeTimeProvider();
        var c = new PassthroughCaptureCoalescer(time);
        var t0 = time.GetUtcNow();
        var (group, _) = c.Join(DeviceIp, Telemetry, 443);
        time.Advance(TimeSpan.FromSeconds(7));
        c.Join(DeviceIp, Telemetry, 443);

        group.Complete(4500, 5300, t0.AddMilliseconds(300));
        var totals = group.Complete(4600, 5200, t0.AddSeconds(7.25));

        Assert.Equal(new PassthroughTotals(2, 9100, 10500, 7250), totals);
    }

    [Fact]
    public void Snapshot_IsNullUntilRecorded_ThenCopiesMetadata()
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());
        var (group, _) = c.Join(DeviceIp, Telemetry, 443);
        Assert.Null(group.Snapshot());

        var id = Guid.NewGuid();
        var metadata = new TlsMetadata { Ja3Hash = "0123456789abcdef0123456789abcdef" };
        group.Recorded(id, "passthrough: auto", metadata);
        metadata.ClientToServerBytes = 999; // the first connection keeps using its own instance

        var row = group.Snapshot()!.Value;
        Assert.Equal(id, row.CaptureId);
        Assert.Equal("passthrough: auto", row.StatusMessage);
        Assert.Equal("0123456789abcdef0123456789abcdef", row.Metadata.Ja3Hash);
        Assert.Equal(0, row.Metadata.ClientToServerBytes);
    }

    [Fact]
    public void DroppedAndPassedThroughConnections_GetSeparateGroups()
    {
        var c = new PassthroughCaptureCoalescer(new FakeTimeProvider());
        c.Join(DeviceIp, Telemetry, 443);

        Assert.True(c.Join(DeviceIp, Telemetry, 443, PassthroughCaptureCoalescer.Dropped).IsFirst);
        Assert.False(c.Join(DeviceIp, Telemetry, 443, PassthroughCaptureCoalescer.Dropped).IsFirst);
    }
}
