using System.Collections.Concurrent;
using System.Net.Sockets;
using IoTSpy.Core.Models;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace IoTSpy.Proxy.Resilience;

public interface IPerHostConnectPipelineCache
{
    ResiliencePipeline GetPipeline(string host, int port);
}

/// <summary>
/// Lazily creates and caches one <see cref="ResiliencePipeline"/> per upstream host:port
/// so that a broken circuit on one endpoint never blocks connections to other hosts —
/// or other ports on the same host (e.g. a dead service on :80 must not trip the breaker
/// for an unrelated, healthy service on :3000 at the same IP).
/// </summary>
public sealed class PerHostConnectPipelineCache(ResilienceOptions opts) : IPerHostConnectPipelineCache
{
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ResiliencePipeline GetPipeline(string host, int port) =>
        _cache.GetOrAdd($"{host}:{port}", _ => Build());

    private ResiliencePipeline Build() =>
        new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(opts.ConnectTimeoutSeconds)
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = opts.RetryCount,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(opts.RetryBaseDelaySeconds),
                ShouldHandle = new PredicateBuilder()
                    .Handle<SocketException>()
                    .Handle<TimeoutRejectedException>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = opts.CircuitBreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(opts.CircuitBreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(opts.CircuitBreakerBreakSeconds),
                MinimumThroughput = 3,
                ShouldHandle = new PredicateBuilder()
                    .Handle<SocketException>()
                    .Handle<TimeoutRejectedException>()
            })
            .Build();
}
