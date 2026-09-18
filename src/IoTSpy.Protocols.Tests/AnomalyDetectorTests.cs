using Xunit;
using IoTSpy.Core.Models;
using IoTSpy.Protocols.Anomaly;

namespace IoTSpy.Protocols.Tests;

public class AnomalyDetectorTests
{
    // ── Warm-up suppression ───────────────────────────────────────────────────

    [Fact]
    public void Record_BelowWarmUp_NoAlerts()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 30, DeviationThreshold = 3.0 };

        // Feed 29 normal observations — no alerts expected
        for (var i = 0; i < 29; i++)
            Assert.Empty(detector.Record("host-a", 100, 1024, 200));
    }

    [Fact]
    public void Record_ExactlyAtWarmUp_AlertsCanFire()
    {
        // Low threshold so we're sure an anomaly fires once warm-up is reached
        var detector = new AnomalyDetector { WarmUpSamples = 5, DeviationThreshold = 0.5 };

        // 4 observations of 100 ms / 1024 bytes → establish baseline
        for (var i = 0; i < 4; i++)
            detector.Record("h", 100, 1024, 200);

        // 5th observation is an extreme outlier
        var alerts = detector.Record("h", 99999, 1024, 200);

        Assert.NotEmpty(alerts);
        Assert.Contains(alerts, a => a.AlertType == "ResponseTime");
    }

    // ── Response-time anomaly ─────────────────────────────────────────────────

    [Fact]
    public void Record_NormalDuration_NoAlert()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        // Establish baseline: 50 ms ± small noise
        for (var i = 0; i < 10; i++)
            detector.Record("h", 50 + (i % 3), 1024, 200);

        // 53 ms is well within 3 sigma — no alert
        var alerts = detector.Record("h", 53, 1024, 200);
        Assert.DoesNotContain(alerts, a => a.AlertType == "ResponseTime");
    }

    [Fact]
    public void Record_ExtremeDuration_AlertFired()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 50, 1024, 200);

        // 10 000 ms is many sigma above the 50 ms baseline
        var alerts = detector.Record("h", 10000, 1024, 200);

        Assert.Contains(alerts, a => a.AlertType == "ResponseTime" && a.Host == "h");
    }

    [Fact]
    public void Record_DurationAlert_HasCorrectFields()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("target.example.com", 100, 2048, 200);

        var alerts = detector.Record("target.example.com", 50000, 2048, 200);

        var durationAlert = alerts.FirstOrDefault(a => a.AlertType == "ResponseTime");
        Assert.NotNull(durationAlert);
        Assert.Equal("target.example.com", durationAlert.Host);
        Assert.True(durationAlert.ActualValue > durationAlert.ExpectedValue);
        Assert.True(durationAlert.DeviationFactor >= 3.0);
        Assert.True(durationAlert.DetectedAt > DateTimeOffset.MinValue);
    }

    // ── Response-size anomaly ─────────────────────────────────────────────────

    [Fact]
    public void Record_ExtremeSize_AlertFired()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1000, 200);

        var alerts = detector.Record("h", 100, 10_000_000, 200);

        Assert.Contains(alerts, a => a.AlertType == "ResponseSize");
    }

    [Fact]
    public void Record_NormalSize_NoSizeAlert()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1000 + i, 200);

        var alerts = detector.Record("h", 100, 1005, 200);
        Assert.DoesNotContain(alerts, a => a.AlertType == "ResponseSize");
    }

    // ── Status-code anomaly ───────────────────────────────────────────────────

    [Fact]
    public void Record_UnexpectedStatusClass_AlertFired()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        // Baseline: all 200 OK
        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1024, 200);

        // Suddenly a 500 — very unexpected
        var alerts = detector.Record("h", 100, 1024, 500);
        Assert.Contains(alerts, a => a.AlertType == "StatusCode");
    }

    [Fact]
    public void Record_SameStatusClass_NoStatusAlert()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1024, 200);

        // 201 Created is in the same 2xx class
        var alerts = detector.Record("h", 100, 1024, 201);
        Assert.DoesNotContain(alerts, a => a.AlertType == "StatusCode");
    }

    [Fact]
    public void Record_FrequentStatusCode_NotFlaggedAsAnomaly()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        // Establish baseline with mixed 200 and 404 (404 becomes common)
        for (var i = 0; i < 5; i++) detector.Record("h", 100, 1024, 200);
        for (var i = 0; i < 5; i++) detector.Record("h", 100, 1024, 404);

        // 404 is now dominant or near-dominant — should not trigger status alert
        var alerts = detector.Record("h", 100, 1024, 404);
        // The code may or may not fire depending on ratio; key assertion is no duplicate firing
        // What we care about: if 404 has > 5% share, it should NOT be flagged
        Assert.DoesNotContain(alerts, a => a.AlertType == "StatusCode" && a.ActualValue == 404);
    }

    // ── Multiple alerts from single observation ───────────────────────────────

    [Fact]
    public void Record_MultipleAnomalies_AllAlertsReturned()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 10, DeviationThreshold = 3.0 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1000, 200);

        // Extreme outlier on all dimensions
        var alerts = detector.Record("h", 100000, 100_000_000, 500);

        Assert.True(alerts.Count >= 2, $"Expected >=2 alerts, got {alerts.Count}");
    }

    // ── Baseline state ────────────────────────────────────────────────────────

    [Fact]
    public void GetBaselines_ReturnsAllHosts()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };

        detector.Record("host-a", 100, 1024, 200);
        detector.Record("host-b", 200, 2048, 200);
        detector.Record("host-a", 110, 1024, 200);

        var baselines = detector.GetBaselines();
        Assert.True(baselines.ContainsKey("host-a"));
        Assert.True(baselines.ContainsKey("host-b"));
        Assert.Equal(2, baselines["host-a"].SampleCount);
        Assert.Equal(1, baselines["host-b"].SampleCount);
    }

    [Fact]
    public void GetBaselines_MeanConverges()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };
        const int n = 100;
        const double expected = 250.0;

        for (var i = 0; i < n; i++)
            detector.Record("h", expected, 1024, 200);

        var baseline = detector.GetBaselines()["h"];
        Assert.Equal(n, baseline.SampleCount);
        Assert.Equal(expected, baseline.DurationMean, precision: 6);
    }

    [Fact]
    public void GetBaselines_StdDevIsZeroForConstantInput()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };

        for (var i = 0; i < 20; i++)
            detector.Record("h", 100, 1024, 200);

        var baseline = detector.GetBaselines()["h"];
        Assert.Equal(0, baseline.DurationStdDev, precision: 9);
        Assert.Equal(0, baseline.SizeStdDev, precision: 9);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsBaseline()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };

        for (var i = 0; i < 10; i++)
            detector.Record("h", 100, 1024, 200);

        Assert.True(detector.GetBaselines().ContainsKey("h"));

        detector.Reset("h");

        Assert.False(detector.GetBaselines().ContainsKey("h"));
    }

    [Fact]
    public void Reset_UnknownHost_DoesNotThrow()
    {
        var detector = new AnomalyDetector();
        var ex = Record.Exception(() => detector.Reset("non-existent-host"));
        Assert.Null(ex);
    }

    // ── Independent host baselines ────────────────────────────────────────────

    [Fact]
    public void Record_SeparateHosts_IndependentBaselines()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5, DeviationThreshold = 3.0 };

        // Host A: fast responses
        for (var i = 0; i < 10; i++) detector.Record("fast-host", 10, 512, 200);
        // Host B: slow responses
        for (var i = 0; i < 10; i++) detector.Record("slow-host", 5000, 10240, 200);

        var fastBaseline = detector.GetBaselines()["fast-host"];
        var slowBaseline = detector.GetBaselines()["slow-host"];

        Assert.Equal(10, fastBaseline.DurationMean, precision: 6);
        Assert.Equal(5000, slowBaseline.DurationMean, precision: 6);

        // Anomaly on fast-host should not affect slow-host
        var alerts = detector.Record("fast-host", 50000, 512, 200);
        Assert.Contains(alerts, a => a.Host == "fast-host" && a.AlertType == "ResponseTime");
    }

    // ── SnapshotBaselines ────────────────────────────────────────────────────

    [Fact]
    public void SnapshotBaselines_ReturnsDefensiveCopyOfStatusCodeHistogram()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };
        detector.Record("h", 100, 1024, 200);
        detector.Record("h", 100, 1024, 200);
        detector.Record("h", 100, 1024, 404);

        var snapshot = detector.SnapshotBaselines().Single(s => s.Host == "h");

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(2, snapshot.StatusCodeCounts[200]);
        Assert.Equal(1, snapshot.StatusCodeCounts[404]);

        // Mutating the snapshot's dictionary must not affect the live baseline.
        var mutable = (Dictionary<int, long>)snapshot.StatusCodeCounts;
        mutable[999] = 42;
        var secondSnapshot = detector.SnapshotBaselines().Single(s => s.Host == "h");
        Assert.False(secondSnapshot.StatusCodeCounts.ContainsKey(999));
    }

    [Fact]
    public void SnapshotBaselines_ExcludesEmptyDetector()
    {
        var detector = new AnomalyDetector();
        Assert.Empty(detector.SnapshotBaselines());
    }

    [Fact]
    public async Task SnapshotBaselines_ConcurrentWithRecord_DoesNotThrowOrCorrupt()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 5 };
        // Seed a few hosts so there's something to snapshot from the start.
        for (var i = 0; i < 5; i++)
        {
            detector.Record("host-a", 100, 1024, 200);
            detector.Record("host-b", 100, 1024, 200);
        }

        using var cts = new CancellationTokenSource();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 5000 && !cts.IsCancellationRequested; i++)
                {
                    detector.Record("host-a", 100 + i % 7, 1024 + i % 13, 200 + (i % 3 == 0 ? 4 : 0));
                    detector.Record("host-b", 50, 512, 200);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, TestContext.Current.CancellationToken);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 2000 && !cts.IsCancellationRequested; i++)
                {
                    var snapshots = detector.SnapshotBaselines();
                    foreach (var s in snapshots)
                    {
                        // Force full enumeration of the defensive copy — this must never
                        // throw InvalidOperationException from concurrent mutation.
                        long total = 0;
                        foreach (var kv in s.StatusCodeCounts) total += kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await writer;
        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(exceptions);
    }

    // ── Seed (restore from persisted checkpoint) ──────────────────────────────

    [Fact]
    public void Seed_RestoresBaselineState()
    {
        var detector = new AnomalyDetector { WarmUpSamples = 30 };
        var firstSeen = DateTimeOffset.UtcNow.AddDays(-1);

        detector.Seed([
            new HostBaselineRecord
            {
                Host = "restored-host",
                SampleCount = 500,
                FirstSeenAt = firstSeen,
                DurationMean = 42.0,
                DurationM2 = 10.0,
                SizeMean = 2048.0,
                SizeM2 = 100.0,
                StatusCodeCountsJson = "{\"200\":450,\"500\":50}",
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        ]);

        var baseline = detector.GetBaselines()["restored-host"];
        Assert.Equal(500, baseline.SampleCount);
        Assert.Equal(firstSeen, baseline.FirstSeenAt);
        Assert.Equal(42.0, baseline.DurationMean);
        Assert.Equal(450, baseline.StatusCodeCounts[200]);
        Assert.Equal(50, baseline.StatusCodeCounts[500]);
    }

    [Fact]
    public void Seed_HighSampleCount_SkipsWarmUpGate_AlertsFireImmediately()
    {
        // WarmUpSamples is keyed on SampleCount — seeding a high SampleCount should mean
        // the very next Record() call is already past warm-up, unlike a cold host which
        // would need WarmUpSamples observations first.
        var detector = new AnomalyDetector { WarmUpSamples = 1000, DeviationThreshold = 0.5 };

        detector.Seed([
            new HostBaselineRecord
            {
                Host = "warm-host",
                SampleCount = 2000,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
                DurationMean = 100.0,
                DurationM2 = 10.0, // small variance -> tight std dev
                SizeMean = 1024.0,
                SizeM2 = 10.0,
                StatusCodeCountsJson = "{\"200\":2000}",
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        ]);

        // A single observation is enough to fire an alert — no re-warm-up needed.
        var alerts = detector.Record("warm-host", 99999, 1024, 200);
        Assert.Contains(alerts, a => a.AlertType == "ResponseTime");
    }

    [Fact]
    public void Seed_MalformedHistogramJson_DoesNotThrow_FallsBackToEmpty()
    {
        var detector = new AnomalyDetector();
        var ex = Xunit.Record.Exception(() => detector.Seed([
            new HostBaselineRecord
            {
                Host = "bad-json-host",
                SampleCount = 10,
                FirstSeenAt = DateTimeOffset.UtcNow,
                StatusCodeCountsJson = "not-json",
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        ]));

        Assert.Null(ex);
        Assert.Empty(detector.GetBaselines()["bad-json-host"].StatusCodeCounts);
    }

    // ── Rate-alert-after-restore regression (FirstSeenAt fix) ─────────────────

    [Fact]
    public void Record_RateBurst_AfterSeedingLargeSampleCount_StillFiresRequestRateAlert()
    {
        // Regression test: before the FirstSeenAt fix, the historical-rate denominator was
        // bounded near RateWindowSeconds regardless of SampleCount, so seeding a large
        // persisted SampleCount made historicalRate scale unboundedly with SampleCount,
        // permanently preventing the RequestRate alert from firing for that host.
        var detector = new AnomalyDetector { WarmUpSamples = 30, DeviationThreshold = 3.0, RateWindowSeconds = 60 };

        // Simulate a host that has been running (and persisted) for a long time with a
        // low, steady request rate: many samples, but spread over a long FirstSeenAt.
        detector.Seed([
            new HostBaselineRecord
            {
                Host = "rate-host",
                SampleCount = 100_000,
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-30), // long history, low average rate
                DurationMean = 50.0,
                DurationM2 = 5.0,
                SizeMean = 1024.0,
                SizeM2 = 5.0,
                StatusCodeCountsJson = "{\"200\":100000}",
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        ]);

        // Historical rate ≈ 100000 / (30*86400) ≈ 0.0386 req/s.
        // A burst of many requests in a tight loop should push the windowed rate well above
        // (1 + threshold) * historicalRate and fire a RequestRate alert.
        var allAlerts = new List<AnomalyAlert>();
        for (var i = 0; i < 50; i++)
            allAlerts.AddRange(detector.Record("rate-host", 50, 1024, 200));

        Assert.Contains(allAlerts, a => a.AlertType == "RequestRate");
    }
}
