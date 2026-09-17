using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace IoTSpy.Api.Services;

/// <summary>
/// Defers proxy capture persistence to avoid one DB round-trip per HTTP request.
///
/// The proxy hot path calls <see cref="TryEnqueue"/> (non-blocking), which writes
/// to a bounded <see cref="Channel{T}"/>. The background consumer drains the channel
/// in configurable batches, performing a single <c>AddRange + SaveChangesAsync</c>
/// per flush — reducing DB commits by up to two orders of magnitude at high traffic.
///
/// SignalR publishing also moves off the hot path: after each batch is persisted, the
/// consumer publishes each capture individually via <see cref="ICapturePublisher"/>.
///
/// If the channel is full (sustained overload), the oldest enqueued entry is evicted
/// to make room for the incoming one. This preserves recency with graceful degradation
/// rather than blocking the proxy request thread.
/// </summary>
public sealed class CaptureBatchWriter : BackgroundService, ICaptureBatchWriter
{
    private readonly Channel<CapturedRequest> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICapturePublisher _publisher;
    private readonly ILogger<CaptureBatchWriter> _logger;

    private const int ChannelCapacity = 20_000;
    private const int BatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    // NOTE: BoundedChannelFullMode.DropOldest means TryWrite always returns true
    // (it evicts the oldest item to make room), so drops are not individually
    // observable at the call site. If the channel consistently stays full
    // (flush rate < enqueue rate), old captures are silently lost. Monitor
    // channel depth via metrics/logs if sustained high-throughput capture is needed.

    public CaptureBatchWriter(
        IServiceScopeFactory scopeFactory,
        ICapturePublisher publisher,
        ILogger<CaptureBatchWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;

        _channel = Channel.CreateBounded<CapturedRequest>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false  // multiple proxy connections write concurrently
        });
    }

    /// <inheritdoc/>
    public bool TryEnqueue(CapturedRequest capture) => _channel.Writer.TryWrite(capture);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<CapturedRequest>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for first item or until the flush interval elapses.
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                flushCts.CancelAfter(FlushInterval);

                try
                {
                    await _channel.Reader.WaitToReadAsync(flushCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Flush interval elapsed — drain whatever is available now.
                }

                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count > 0)
                {
                    await PersistAndPublishAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capture batch writer encountered an error; retrying after delay");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }

        // Graceful shutdown: drain remaining items.
        while (_channel.Reader.TryRead(out var item))
            batch.Add(item);

        if (batch.Count > 0)
            await PersistAndPublishAsync(batch, CancellationToken.None);
    }

    private async Task PersistAndPublishAsync(List<CapturedRequest> batch, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICaptureRepository>();
            await repo.AddBatchAsync(batch, ct);

            foreach (var capture in batch)
                IoTSpyMetrics.RecordCapture(capture.Protocol.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist batch of {Count} captures to the database", batch.Count);
            // Continue to publish — partial data in the UI is better than none.
        }

        foreach (var capture in batch)
        {
            try { await _publisher.PublishAsync(capture, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to publish capture via SignalR"); }
        }
    }
}
