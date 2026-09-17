using Prometheus;
using Xunit;

namespace IoTSpy.Manipulation.Tests;

/// <summary>
/// Verifies every ManipulationMetrics helper is callable without throwing and actually
/// updates the underlying prometheus-net collector, scraped via the shared default
/// registry (see IoTSpy.Api.Tests.Services.IoTSpyMetricsTests for the same pattern).
/// </summary>
public class ManipulationMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task RecordBreakpointHit_DoesNotThrow_AndIncrementsCounter()
    {
        var breakpointId = Guid.NewGuid();

        ManipulationMetrics.RecordBreakpointHit(breakpointId);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_breakpoint_hits_total", text);
        Assert.Contains($"breakpoint_id=\"{breakpointId}\"", text);
    }

    [Fact]
    public async Task RecordRuleMatch_DoesNotThrow_AndIncrementsCounter()
    {
        var ruleId = Guid.NewGuid();

        ManipulationMetrics.RecordRuleMatch(ruleId);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_rule_matches_total", text);
        Assert.Contains($"rule_id=\"{ruleId}\"", text);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("anomaly")]
    [InlineData("error")]
    public async Task RecordFuzzerRequest_DoesNotThrow_AndIncrementsCounter(string status)
    {
        ManipulationMetrics.RecordFuzzerRequest(status);

        var text = await ScrapeAsync();
        Assert.Contains("iotspy_fuzzer_requests_total", text);
        Assert.Contains($"status=\"{status}\"", text);
    }
}
