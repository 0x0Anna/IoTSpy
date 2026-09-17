using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace IoTSpy.Api.Services;

/// <summary>
/// Defers protocol-message persistence to avoid one DB round-trip per decoded MQTT/DNS
/// message. Same shape as <see cref="CaptureBatchWriter"/> — the proxy hot path calls
/// <see cref="TryEnqueue"/> (non-blocking), a background consumer drains a bounded
/// <see cref="Channel{T}"/> in batches, and the oldest buffered message is dropped under
/// sustained overload rather than blocking the caller.
/// </summary>
public sealed class ProtocolMessageBatchWriter : BackgroundService, IProtocolMessageWriter
{
    private readonly Channel<PersistedProtocolMessage> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProtocolMessageBatchWriter> _logger;

    private const int ChannelCapacity = 20_000;
    private const int BatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    public ProtocolMessageBatchWriter(IServiceScopeFactory scopeFactory, ILogger<ProtocolMessageBatchWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _channel = Channel.CreateBounded<PersistedProtocolMessage>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false // multiple proxy connections write concurrently
        });
    }

    /// <inheritdoc/>
    public bool TryEnqueue(PersistedProtocolMessage message) => _channel.Writer.TryWrite(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<PersistedProtocolMessage>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
                    await PersistAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Protocol message batch writer encountered an error; retrying after delay");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }

        // Graceful shutdown: drain remaining items.
        while (_channel.Reader.TryRead(out var item))
            batch.Add(item);

        if (batch.Count > 0)
            await PersistAsync(batch, CancellationToken.None);
    }

    private async Task PersistAsync(List<PersistedProtocolMessage> batch, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProtocolMessageRepository>();
            await repo.AddBatchAsync(batch, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist batch of {Count} protocol messages to the database", batch.Count);
        }
    }
}
