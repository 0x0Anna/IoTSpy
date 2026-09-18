using System.Net.Sockets;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace IoTSpy.Proxy.Resilience;

/// <summary>
/// DI extension that registers two named resilience pipelines used by
/// <see cref="IoTSpy.Proxy.Interception.ExplicitProxyServer"/>:
/// <list type="bullet">
///   <item><term>iotspy-connect</term><description>Per-host keyed pipeline:
///     Timeout → Retry → CircuitBreaker for TCP connect calls.</description></item>
///   <item><term>iotspy-tls</term><description>Timeout-only pipeline for TLS
///     handshakes (not safely retryable).</description></item>
/// </list>
/// </summary>
public static class ProxyResiliencePipelines
{
    /// <summary>Key for the per-host TCP connect pipeline.</summary>
    public const string ConnectPipelineKey = "iotspy-connect";

    /// <summary>Key for the TLS handshake pipeline.</summary>
    public const string TlsPipelineKey = "iotspy-tls";

    /// <summary>
    /// Registers both resilience pipelines with the DI container.
    /// </summary>
    public static IServiceCollection AddProxyResilience(
        this IServiceCollection services,
        ResilienceOptions opts,
        UpstreamConnectionPoolOptions? poolOpts = null)
    {
        // Per-host connect pipeline: keyed on the upstream hostname so that a
        // dead IoT cloud endpoint does not trip the circuit breaker for other hosts.
        services.AddResiliencePipeline<string>(ConnectPipelineKey, (builder, ctx) =>
        {
            builder
                // Inner: hard timeout on the connect attempt itself
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(opts.ConnectTimeoutSeconds)
                })
                // Middle: retry with exponential back-off
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = opts.RetryCount,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(opts.RetryBaseDelaySeconds),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<SocketException>()
                        .Handle<TimeoutRejectedException>()
                })
                // Outer: circuit breaker
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = opts.CircuitBreakerFailureRatio,
                    SamplingDuration = TimeSpan.FromSeconds(opts.CircuitBreakerSamplingSeconds),
                    BreakDuration = TimeSpan.FromSeconds(opts.CircuitBreakerBreakSeconds),
                    MinimumThroughput = 3,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<SocketException>()
                        .Handle<TimeoutRejectedException>()
                });
        });

        // TLS handshake pipeline: timeout only — TLS auth is not safely retryable.
        services.AddResiliencePipeline(TlsPipelineKey, builder =>
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(opts.TlsHandshakeTimeoutSeconds)
            });
        });

        services.AddSingleton<IPerHostConnectPipelineCache>(new PerHostConnectPipelineCache(opts));

        services.AddSingleton(poolOpts ?? new UpstreamConnectionPoolOptions());
        services.AddSingleton<UpstreamConnectionPool>();
        services.AddSingleton<IUpstreamConnectionPool>(sp => sp.GetRequiredService<UpstreamConnectionPool>());
        services.AddHostedService<UpstreamConnectionPoolEvictionService>();

        return services;
    }
}
