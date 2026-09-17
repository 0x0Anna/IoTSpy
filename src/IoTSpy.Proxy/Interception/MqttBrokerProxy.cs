using System.Net;
using System.Net.Sockets;
using System.Text;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Protocols.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Proxy.Interception;

/// <summary>
/// Transparent MQTT MITM proxy. Sits between MQTT clients and an upstream broker,
/// parsing all MQTT packets for inspection. Supports topic-level filtering.
/// </summary>
public class MqttBrokerProxy(
    ICapturePublisher publisher,
    IProtocolMessageWriter protocolMessageWriter,
    IServiceScopeFactory scopeFactory,
    ILogger<MqttBrokerProxy> logger) : IMqttBrokerProxy
{
    private const int PayloadPreviewMaxLength = 2048;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;
    private MqttBrokerSettings? _settings;
    private readonly MqttDecoder _decoder = new();

    public bool IsRunning => _listener is not null;
    public int ActiveConnections => _activeConnections;

    public async Task StartAsync(MqttBrokerSettings settings, CancellationToken ct = default)
    {
        if (IsRunning) return;
        if (string.IsNullOrEmpty(settings.UpstreamHost))
            throw new ArgumentException("UpstreamHost is required for MQTT broker proxy");

        _settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var ip = settings.ListenAddress is "0.0.0.0" or "*"
            ? IPAddress.Any
            : IPAddress.Parse(settings.ListenAddress);
        _listener = new TcpListener(ip, settings.ListenPort);
        _listener.Start();
        logger.LogInformation("MQTT broker proxy listening on {Addr}:{Port} → {Upstream}:{UpstreamPort}",
            settings.ListenAddress, settings.ListenPort, settings.UpstreamHost, settings.UpstreamPort);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        logger.LogInformation("MQTT broker proxy stopped ({Count} active connections)", _activeConnections);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "MQTT proxy accept error");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        using (client)
        {
            client.ReceiveTimeout = 60_000;
            client.SendTimeout = 30_000;
            var clientIp = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "unknown";
            // Resolved once per connection, not per message — a scoped repository lookup
            // on every decoded packet would be a needless DB round-trip on the hot path.
            var deviceId = await TryResolveDeviceIdAsync(clientIp, ct);

            TcpClient? upstream = null;
            try
            {
                upstream = new TcpClient();
                await upstream.ConnectAsync(_settings!.UpstreamHost!, _settings.UpstreamPort, ct);

                var clientStream = client.GetStream();
                var upstreamStream = upstream.GetStream();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var clientId = "unknown";

                async Task RelayMqtt(NetworkStream source, NetworkStream dest, bool isFromClient, CancellationToken token)
                {
                    var buffer = new byte[64 * 1024];
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var read = await source.ReadAsync(buffer, token);
                            if (read == 0) break;

                            // Forward data immediately
                            await dest.WriteAsync(buffer.AsMemory(0, read), token);

                            // Parse MQTT packets for inspection
                            try
                            {
                                var data = buffer.AsMemory(0, read);
                                if (_decoder.CanDecode(data.Span))
                                {
                                    var messages = await _decoder.DecodeAsync(data, token);
                                    foreach (var msg in messages)
                                    {
                                        // Track client ID from CONNECT packets
                                        if (msg.PacketType == MqttPacketType.Connect && msg.ClientId != null)
                                            clientId = msg.ClientId;

                                        // Apply topic filter
                                        if (_settings.TopicFilters.Count > 0 && msg.Topic != null)
                                        {
                                            if (!_settings.TopicFilters.Any(f => MatchesTopic(f, msg.Topic)))
                                                continue;
                                        }

                                        var captured = new MqttCapturedMessage
                                        {
                                            ClientId = clientId,
                                            ClientIp = clientIp,
                                            PacketType = msg.PacketType.ToString(),
                                            Topic = msg.Topic,
                                            QoS = (int)msg.QoS,
                                            Retain = msg.Retain,
                                            PayloadText = _settings.LogPayloads && msg.Payload != null
                                                ? Encoding.UTF8.GetString(msg.Payload)
                                                : null,
                                            PayloadSize = msg.Payload?.Length ?? 0,
                                            Direction = isFromClient ? "client→broker" : "broker→client"
                                        };

                                        await publisher.PublishMqttMessageAsync(captured, token);
                                        logger.LogDebug("MQTT {Dir} {PacketType} client={ClientId} topic={Topic}",
                                            captured.Direction, msg.PacketType, clientId, msg.Topic);

                                        protocolMessageWriter.TryEnqueue(BuildPersistedMessage(captured, deviceId));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogTrace(ex, "MQTT decode error (non-fatal)");
                            }
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        logger.LogTrace(ex, "MQTT relay ended ({Dir})", isFromClient ? "client→broker" : "broker→client");
                    }
                    finally
                    {
                        await cts.CancelAsync();
                    }
                }

                await Task.WhenAll(
                    RelayMqtt(clientStream, upstreamStream, isFromClient: true, cts.Token),
                    RelayMqtt(upstreamStream, clientStream, isFromClient: false, cts.Token));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "MQTT proxy client handler error for {Ip}", clientIp);
            }
            finally
            {
                upstream?.Dispose();
            }
        }
        Interlocked.Decrement(ref _activeConnections);
    }

    private async Task<Guid?> TryResolveDeviceIdAsync(string clientIp, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var device = await devices.GetByIpAsync(clientIp, ct);
            return device?.Id;
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "MQTT device lookup failed for {ClientIp} (non-fatal)", clientIp);
            return null;
        }
    }

    private static PersistedProtocolMessage BuildPersistedMessage(MqttCapturedMessage captured, Guid? deviceId) =>
        new()
        {
            DeviceId = deviceId,
            Protocol = InterceptionProtocol.Mqtt,
            Direction = captured.Direction,
            Subject = captured.Topic,
            Summary = $"{captured.PacketType} qos={captured.QoS} retain={captured.Retain} payloadLen={captured.PayloadSize}",
            PayloadPreview = captured.PayloadText is { Length: > 0 } text
                ? text[..Math.Min(text.Length, PayloadPreviewMaxLength)]
                : null,
            Timestamp = captured.Timestamp
        };

    /// <summary>
    /// Matches an MQTT topic against a filter pattern (supports + and # wildcards).
    /// </summary>
    public static bool MatchesTopic(string filter, string topic)
    {
        var filterParts = filter.Split('/');
        var topicParts = topic.Split('/');

        for (var i = 0; i < filterParts.Length; i++)
        {
            if (filterParts[i] == "#") return true; // multi-level wildcard
            if (i >= topicParts.Length) return false;
            if (filterParts[i] == "+") continue; // single-level wildcard
            if (filterParts[i] != topicParts[i]) return false;
        }

        return filterParts.Length == topicParts.Length;
    }
}
