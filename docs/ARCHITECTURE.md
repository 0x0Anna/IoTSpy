# IoTSpy — Architecture

## Overview

IoTSpy is a .NET 10 / C# solution that acts as a transparent MITM proxy, multi-protocol decoder, statistical anomaly detector, and lightweight pen-test suite for IoT network research. A REST + SignalR API exposes all functionality; the frontend is a Vite 6 + React 19 + TypeScript single-page application.

All phases (1–16, 18–22) are complete and production-ready, plus post-phase content rules decoupling. Phase 17 (non-IP IoT protocol expansion) has been archived. See [PHASES-COMPLETED.md](PHASES-COMPLETED.md) for full details.

---

## Solution structure

```
IoTSpy.sln
src/
  IoTSpy.Core/           — domain models, interfaces, enums (no infrastructure deps)
  IoTSpy.Proxy/          — TCP listeners, TLS MITM, TLS passthrough + JA3, SSL stripping, Polly resilience
  IoTSpy.Protocols/      — protocol decoders + anomaly detection
  IoTSpy.Scanner/        — pen-test suite (port scan, CVE lookup, config audit)
  IoTSpy.Manipulation/   — rules engine, scripted breakpoints, replay, fuzzer, AI mock, API spec generation, content replacement
  IoTSpy.Storage/        — EF Core DbContext + repositories (SQLite / PostgreSQL)
  IoTSpy.Api/            — ASP.NET Core host (REST + SignalR)
  IoTSpy.Protocols.Tests/
  IoTSpy.Manipulation.Tests/
  IoTSpy.Scanner.Tests/
  IoTSpy.Api.Tests/
  IoTSpy.Proxy.Tests/
  IoTSpy.Storage.Tests/
  IoTSpy.Api.IntegrationTests/
frontend/                — Vite 6 + React 19 + TypeScript SPA
docs/
```

---

## Project dependency graph

```
IoTSpy.Api
  ├── IoTSpy.Core          ← domain only; no infrastructure deps
  ├── IoTSpy.Proxy         ← BouncyCastle, SharpPcap, Polly, IoTSpy.Protocols
  ├── IoTSpy.Storage       ← EF Core 10, Npgsql
  ├── IoTSpy.Protocols     ← IoTSpy.Core only
  ├── IoTSpy.Scanner       ← IoTSpy.Core, Microsoft.Extensions.Http
  └── IoTSpy.Manipulation  ← IoTSpy.Core, Roslyn, Jint, Microsoft.Extensions.Http
```

Test projects reference only the library under test (plus xunit 2.9.3).

---

## IoTSpy.Core

Pure domain layer — no NuGet dependencies beyond the BCL.

### Models (30+)

Domain models live in `IoTSpy.Core/Models/`; DTOs and result types are co-located with their interfaces in `IoTSpy.Core/Interfaces/`.

| Model | Purpose |
|---|---|
| `CapturedRequest` | One intercepted HTTP/HTTPS exchange; includes request + response headers/body, TLS flag, protocol, timestamp, durationMs, `IsModified`, `TlsMetadataJson` |
| `Device` | IoT device record: IP, MAC, hostname, vendor, label, `SecurityScore` |
| `ProxySettings` | Singleton config row (Id=1): ports, mode, TLS/body capture flags, PBKDF2 password hash, `SslStrip` toggle, `AutoStart` flag |
| `TlsMetadata` | All metadata extracted from TLS handshake during passthrough mode: SNI, JA3/JA3S, cipher suites, server cert details, byte counts (serialized as JSON in `CapturedRequest.TlsMetadataJson`) |
| `User` | Multi-user RBAC identity: username, PBKDF2 password hash, `UserRole` (Admin/Operator/Viewer), `IsEnabled`, timestamps |
| `AuditEntry` | Immutable audit trail entry: actor user ID, action name, target, details JSON, timestamp |
| `DashboardLayout` | Per-user saved dashboard configuration: JSON-serialized panel layout and filter state |
| `CertificateEntry` | PEM-encoded root CA or per-host leaf cert with validity range |
| `ResilienceOptions` | Typed options for Polly pipeline configuration |
| `ScanJob` | A scanner run: target IP, port range, which checks to run, status, result counts |
| `ScanFinding` | One finding from a scan: type, severity, port, service, banner, CPE, CVE |
| `ManipulationRule` | Declarative traffic rewrite rule: match patterns → action |
| `Breakpoint` | C# or JS script invoked on matching traffic |
| `HttpMessage` | Mutable HTTP message used within the manipulation pipeline |
| `ReplaySession` | Re-played capture: modified request + recorded response |
| `FuzzerJob` | A fuzzer run: base capture, strategy, concurrency, status |
| `FuzzerResult` | One fuzzer mutation result with anomaly flag |
| `AiMockResponse` | LLM-generated mock HTTP response |
| `AiProviderConfig` | AI provider selection and credentials |
| `AnomalyAlert` | Statistical anomaly: host, type, expected/actual values, deviation factor |
| `HostBaseline` | Per-host Welford running state: duration/size mean+M2, status distribution, request timestamps |
| `OpenRtbEvent` | Captured OpenRTB bid request/response with decoded fields |
| `OpenRtbPiiPolicy` | PII redaction policy: field path, strategy, auto-apply flag |
| `PiiStrippingLog` | Audit log entry for PII redaction actions |
| `ScheduledScan` | Cron-scheduled recurring scan job (Phase 9) |
| `CaptureDevice` | Network interface available for packet capture (name, IPs, MAC) |
| `CapturedPacket` | Captured network packet with L2-L4 fields, protocol flags, `[NotMapped] RawData` |
| `PacketFilter` | Filter criteria for captured packet queries |
| `NetworkDevice` | Discovered network device from packet capture |
| `WebSocketFrame` | Captured WebSocket frame with opcode, payload, direction |
| `MqttCapturedMessage` | Captured MQTT message with topic, payload, QoS |
| `MqttBrokerSettings` | Configuration for the MQTT broker proxy |
| `CoapProxySettings` | Configuration for the CoAP UDP proxy |
| `PacketFilterDto` | API-facing filter DTO: protocol, IP, port, time range, payload search |
| `FreezeFrameResult` | Frozen packet with hex dump and per-layer breakdown |
| `ProtocolDistribution` / `ProtocolStats` | Protocol distribution statistics |
| `CommunicationPattern` | Source→dest pair with packet count, bytes, protocols, time range |
| `SuspiciousActivity` | Detection result: category, severity, evidence list |
| `NetworkDeviceStatistics` | Live capture stats: PPS, BPS, drop rate |

### Interfaces (33+)

`ICaptureRepository`, `IDeviceRepository`, `IProxySettingsRepository`, `ICertificateRepository`, `ICertificateAuthority`, `IProxyService`, `ICapturePublisher`, `IProtocolDecoder<T>`, `IScanJobRepository`, `IScannerService`, `IManipulationRuleRepository`, `IBreakpointRepository`, `IReplaySessionRepository`, `IFuzzerJobRepository`, `IManipulationService`, `IAiMockService`, `IAnomalyDetector`, `IAnomalyAlertPublisher`, `IPacketCaptureService`, `IPacketCaptureAnalyzer`, `IPacketCapturePublisher`, `IOpenRtbEventRepository`, `IOpenRtbPiiPolicyRepository`, `IOpenRtbService`, `IPiiStrippingLogRepository`, `ICaptureDeviceRepository`, `IAlertingService`, `IReportService`, `IScheduledScanRepository`, `IMqttBrokerProxy`, `ICoapProxy`, `IUserRepository`, `IAuditRepository`, `IDashboardLayoutRepository`, `IApiSpecRepository`, `IApiSpecService`, `IManipulationRuleCache`

| `ApiSpecDocument` | API spec entity: name, host, version, OpenAPI JSON, status (Draft/Active/Archived), mock/passthrough/LLM flags, timestamps; nav property to `ContentReplacementRule` list |
| `ContentReplacementRule` | Content replacement rule: match type (ContentType/JsonPath/HeaderValue/BodyRegex), action (ReplaceWithFile/ReplaceWithUrl/ReplaceWithValue/Redact/TrackingPixel/MockSseStream), priority, host/path scope patterns; nullable FK → ApiSpecDocument; `Host` column for standalone rules |
| `ApiSpecGenerationRequest` | DTO for spec generation: host, path pattern, method, date range, LLM flag, name |

### Enums (16)

| Enum | Values |
|---|---|
| `ProxyMode` | ExplicitProxy, ArpSpoof, GatewayRedirect, Passive |
| `UserRole` | Admin, Operator, Viewer |
| `InterceptionProtocol` | Http, Https, Mqtt, MqttTls, CoAP, Dns, MDns, WebSocket, WebSocketTls, Grpc, Modbus, TlsPassthrough, Other |
| `ScanStatus` | Pending, Running, Completed, Failed, Cancelled |
| `ScanFindingType` | OpenPort, ServiceBanner, DefaultCredential, Cve, ConfigIssue |
| `ScanFindingSeverity` | Info, Low, Medium, High, Critical |
| `ManipulationPhase` | Request, Response |
| `ManipulationRuleAction` | ModifyHeader, ModifyBody, OverrideStatusCode, Drop, Delay |
| `ScriptLanguage` | CSharp, JavaScript |
| `FuzzerStrategy` | Random, Boundary, BitFlip |
| `FuzzerJobStatus` | Pending, Running, Completed, Cancelled, Failed |
| `WebSocketOpcode` | Continuation, Text, Binary, Close, Ping, Pong |
| `OpenRtbMessageType` | BidRequest, BidResponse |
| `PiiRedactionStrategy` | Redact, Hash, Mask, Remove |
| `ApiSpecStatus` | Draft, Active, Archived |
| `ContentMatchType` | ContentType, JsonPath, HeaderValue, BodyRegex |
| `ContentReplacementAction` | ReplaceWithFile, ReplaceWithUrl, ReplaceWithValue, Redact, TrackingPixel, MockSseStream |

---

## IoTSpy.Proxy

### Interception servers

| Class | Port | Mode |
|---|---|---|
| `ExplicitProxyServer` | 8888 (configurable) | Device explicitly configured to use proxy |
| `TransparentProxyServer` | 9999 (configurable) | Gateway / ARP-redirected traffic |

Both servers share a unified `InterceptHttpStreamAsync` pipeline: HTTP is intercepted inline; HTTPS CONNECT tunnels fork into a TLS MITM branch when `CaptureTls=true`.

### TLS MITM (`Tls/CertificateAuthority`)

1. Device sends `CONNECT host:443 HTTP/1.1`.
2. Proxy replies `200 Connection established`.
3. `CertificateAuthority.GetOrCreateHostCertAsync(host)` returns or generates a BouncyCastle leaf cert signed by the IoTSpy root CA.
4. `SslStream.AuthenticateAsServerAsync` presents the leaf cert to the device.
5. A second `SslStream.AuthenticateAsClientAsync` connects upstream (validation disabled — research tool).
6. HTTP exchanges are parsed, stored, and forwarded.

Root CA: 4096-bit RSA, self-signed, generated lazily on first use, stored as PEM in `CertificateEntries`, cached in memory.
Leaf certs: 2048-bit RSA, **≤ 397-day validity** (Apple enforces a 398-day cap), SAN-bearing, cached in DB, reused until one day before expiry.

**Apple iOS/macOS compatibility requirements (enforced in `CertificateAuthority`):**
- Validity ≤ 397 days — iOS/macOS silently rejects certs exceeding Apple's 398-day cap
- AKI in **keyid-only form** — use `CreateAuthorityKeyIdentifier(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(caKeyPair.Public))`; iOS 16+ / iOS 26 rejects the full form (keyId + DirName + serial) produced by `CreateAuthorityKeyIdentifier(caCert)`
- IP-address SANs must use `GeneralName.IPAddress` with `DerOctetString(ip.GetAddressBytes())`; iOS rejects `DnsName`-as-IP

If leaf certs were previously generated with the wrong extensions, delete them and let the proxy regenerate:
```bash
sqlite3 src/IoTSpy.Api/iotspy.db "DELETE FROM Certificates WHERE IsRootCa = 0;"
```

### TLS Passthrough (`Tls/TlsClientHelloParser`, `Tls/TlsServerHelloParser`)

When `CaptureTls=false`, traffic is **not** decrypted but metadata is extracted from the TLS handshake:

1. Proxy buffers the initial bytes from the client until a complete TLS record is available (`GetRecordLength()`).
2. `TlsClientHelloParser.TryParse()` extracts:
   - **SNI hostname** (extension type 0x0000)
   - **Cipher suites** and **extensions** lists
   - **Elliptic curves** and **EC point formats**
   - **JA3 fingerprint** — MD5 of `TLSVersion,Ciphers,Extensions,EllipticCurves,EcPointFormats` with GREASE values (RFC 8701) excluded
3. The buffered ClientHello is forwarded to the upstream server.
4. `TlsServerHelloParser.TryParseServerHello()` extracts:
   - **Selected cipher** and **TLS version** (including `supported_versions` extension for TLS 1.3 real version)
   - **JA3S fingerprint** — MD5 of `TLSVersion,Cipher,Extensions`
5. `TlsServerHelloParser.TryParseCertificate()` extracts the leaf X.509 certificate (TLS 1.2 only — Certificate is encrypted in TLS 1.3):
   - Subject, issuer, SAN list, serial number, SHA-256 fingerprint, validity dates
6. Traffic is relayed bidirectionally with byte counting (`ClientToServerBytes`, `ServerToClientBytes`).
7. A `CapturedRequest` is recorded with `Protocol=TlsPassthrough` and `TlsMetadataJson` containing the serialized `TlsMetadata` model.

All log entries include `DnsCorrelationKey={ClientIp}→{SniHostname}` for joining with DNS packet captures.

### SSL Stripping (`Interception/SslStripService`)

When `ProxySettings.SslStrip=true` on **plain HTTP** connections, `SslStripService` intercepts HTTP→HTTPS redirects and serves decrypted content over HTTP — useful for IoT devices that cannot have custom CAs installed.

1. `GetHttpsRedirectLocation()` detects 301/302/303/307/308 responses with `https://` Location headers.
2. `FetchHttpsAsync()` connects to the HTTPS URL upstream, performs a real TLS handshake, and returns the decrypted response.
3. `StripResponseHeaders()` removes `Strict-Transport-Security` headers and rewrites `https://` → `http://` in `Location`, `Set-Cookie`, and `Content-Security-Policy` headers.
4. `StripHttpsFromBody()` rewrites `https://` links in text response bodies (HTML, JSON, etc.).

Integrated into `InterceptHttpStreamAsync` of both `ExplicitProxyServer` and `TransparentProxyServer`. When `SslStrip=true`, HSTS headers are also stripped from all non-redirect responses.

### Network interception helpers

- `ArpSpoofEngine` — SharpPcap-based ARP poisoning to MITM LAN traffic without gateway cooperation.
- `IptablesHelper` — Applies/removes `iptables REDIRECT` rules to redirect device TCP traffic through `TransparentProxyServer`.

### Resilience (`Resilience/ProxyResiliencePipelines`, Polly 8)

Two named pipelines registered in DI:

```
iotspy-connect (keyed by upstream hostname):
  Timeout(ConnectTimeoutSeconds)
    → Retry(RetryCount, exponential, on SocketException | TimeoutRejectedException)
      → CircuitBreaker(per-host, FailureRatio, SamplingDuration, BreakDuration)

iotspy-tls:
  Timeout(TlsHandshakeTimeoutSeconds)   ← TLS handshake not safely retryable
```

All values are configurable under `Resilience` in `appsettings.json`:

| Key | Default |
|---|---|
| ConnectTimeoutSeconds | 15 |
| TlsHandshakeTimeoutSeconds | 10 |
| RetryCount | 2 |
| RetryBaseDelaySeconds | 0.5 |
| CircuitBreakerFailureRatio | 0.5 |
| CircuitBreakerSamplingSeconds | 30 |
| CircuitBreakerBreakSeconds | 60 |

---

## IoTSpy.Protocols

All decoders implement `IProtocolDecoder<T>`:

```csharp
public interface IProtocolDecoder<T>
{
    bool CanDecode(ReadOnlySpan<byte> header);   // fast sniff
    Task<IReadOnlyList<T>> DecodeAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
}
```

### Decoder inventory

| Subdirectory | Decoder | Message type | Protocol / format |
|---|---|---|---|
| `Mqtt/` | `MqttDecoder` | `MqttMessage` | MQTT 3.1.1 and 5.0 (all packet types, QoS, properties); `MqttSessionAnalyzer` accumulates per-topic stats and QoS-2 flow phases |
| `Dns/` | `DnsDecoder` | `DnsMessage` | DNS (RFC 1035) and mDNS (RFC 6762), label decompression, EDNS0 OPT record (`DnsEdnsRecord`: UDP size, DO bit, extended RCODE, EDNS options) |
| `Coap/` | `CoapDecoder` | `CoapMessage` | RFC 7252 full decode; RFC 7959 Block1/Block2 (`CoapBlockOption`), RFC 7641 Observe, `.well-known/core` detection |
| `Telemetry/` | `DatadogDecoder` | `TelemetryMessage` | Datadog v1/v2 series + logs intake JSON |
| `Telemetry/` | `FirehoseDecoder` | `TelemetryMessage` | AWS Kinesis Firehose HTTP PUT (base64 records) |
| `Telemetry/` | `SplunkHecDecoder` | `TelemetryMessage` | Splunk HEC single-event + newline-batched JSON |
| `Telemetry/` | `AzureMonitorDecoder` | `TelemetryMessage` | Azure Monitor array / `records`-wrapped / DCR ingestion payloads |
| `OpenRtb/` | `OpenRtbDecoder` | `OpenRtbEvent` | OpenRTB 2.5 bid request/response parsing |
| `WebSocket/` | `WebSocketDecoder` | `WebSocketDecodedFrame` | RFC 6455 frame decoding; `DetectedSubProtocol: WsSubProtocol?` — STOMP, WAMP, or MQTT-over-WS via payload heuristics |
| `Grpc/` | `GrpcDecoder` | `GrpcMessage` | gRPC Length-Prefixed Message framing + schema-less protobuf field extraction |
| `Modbus/` | `ModbusDecoder` | `ModbusMessage` | Modbus TCP MBAP header, function codes 1-16 + exception responses |
| `MqttSn/` | `MqttSnDecoder` | `MqttSnMessage` | OASIS MQTT-SN v1.2 core message set; short-form and extended (>255-byte) length framing |
| `Rtsp/` | `RtspDecoder` | `RtspMessage` | RFC 2326 request/status lines + headers + `SdpInfo` (RFC 4566) from `application/sdp` bodies |
| `Rtsp/` | `RtpDecoder` | `RtpPacket` | RFC 3550 §5.1 fixed header; unwraps RTSP's `$`-prefixed interleaved binary framing |
| `Amqp/` | `AmqpDecoder` | `AmqpMessage` | AMQP 1.0 protocol-header handshake + all 9 performative types by descriptor code (headline fields only — depth over completeness) |
| `Doh/` | `DohDetector` | `DohDetectionResult` | Detection-only (not `IProtocolDecoder<T>`): RFC 8484 DoH framing on an already-decoded HTTP request, decodes the embedded query via `DnsDecoder` |

`TelemetryMessage` carries: detected protocol, source, timestamp, flat `Fields` map, per-event `Events` list, raw JSON (capped at 8 KB).

DoT (DNS-over-TLS) detection lives alongside the TLS layer, not here: `IoTSpy.Proxy/Tls/DotDetector.IsLikelyDot` flags a ClientHello as likely DoT via destination port 853 or a known-resolver SNI allowlist (encrypted TLS payload gives no further visibility).

### Anomaly detection (`Anomaly/AnomalyDetector`)

Thread-safe, per-host statistical baseline updated with **Welford's online algorithm**. No external dependencies.

**`IAnomalyDetector.Record(host, durationMs, sizeBytes, statusCode)`**
- Updates Welford running mean + M2 for duration and size.
- Maintains status code distribution dictionary.
- Maintains a sliding timestamp queue for rate measurement.
- Returns `IReadOnlyList<AnomalyAlert>` (empty during warm-up period).

**Four alert types:**

| Alert | Trigger |
|---|---|
| `ResponseTime` | `|observed − mean| / stddev ≥ DeviationThreshold` |
| `ResponseSize` | Same formula applied to body size |
| `StatusCode` | Observed HTTP class differs from dominant class **and** dominant code holds ≥ 90% of historical observations |
| `RequestRate` | Current window rate (req/s) exceeds `historicalRate × (1 + DeviationThreshold)` |

Configurable properties: `DeviationThreshold` (default 3.0), `WarmUpSamples` (default 30), `RateWindowSeconds` (default 60).

---

## IoTSpy.Scanner

`ScannerService` orchestrates the full pipeline in a background task; updates `Device.SecurityScore` on completion.

| Class | Responsibility |
|---|---|
| `PortScanner` | TCP connect scan; configurable port ranges and concurrency |
| `ServiceFingerprinter` | Banner grab; CPE string extraction via regex heuristics |
| `CredentialTester` | Default credential checks: FTP, Telnet, MQTT |
| `CveLookupService` | OSV.dev API lookup keyed by CPE |
| `ConfigAuditor` | Detects: Telnet open, UPnP responding, anonymous MQTT, exposed DB ports, HTTP admin interfaces |

### Packet capture (`PacketCaptureService`)

SharpPcap / PacketDotNet-based live packet capture.

| Feature | Details |
|---|---|
| Ring buffer | `LinkedList<CapturedPacket>` with 10,000 packet cap (O(1) enqueue/dequeue) |
| Protocol parsing | Ethernet, IPv4/IPv6, TCP, UDP, ARP, ICMP; app-layer: HTTP, HTTPS, MQTT, DNS, CoAP, DHCP, SSH, Telnet |
| Raw data | `CapturedPacket.RawData` (`[NotMapped]` byte array) stored in-memory for PCAP export |
| Hex dump | Wireshark-style `FormatHexDump` (offset | hex | ASCII) |
| Device sync | `EnsureDevicesEnumeratedAsync()` lazily syncs SharpPcap interfaces to DB on first call |
| Publishing | Fire-and-forget via `PublishSafeAsync` (catches + logs exceptions) |

Registered via `ScannerExtensions.AddIoTSpyScanner()` (all singletons).

> **Linux capability requirement:** SharpPcap requires `CAP_NET_RAW` and `CAP_NET_ADMIN`. Grant them to the *real* dotnet binary (not the symlink — `setcap` rejects symlinks):
> ```bash
> sudo setcap cap_net_raw,cap_net_admin+eip "$(readlink -f $(which dotnet))"
> ```
> Restart the API after running `setcap`. macOS: grant full network access in System Settings → Privacy & Security when prompted.

---

## IoTSpy.Manipulation

`ManipulationService` is the DI-registered orchestrator (`IManipulationService`) and is called inline by both proxy servers for every intercepted exchange.

### Rules engine (`RulesEngine`)

Declarative match → modify rules evaluated in priority order. Match fields: `HostPattern`, `PathPattern`, `MethodPattern` (all regex). Actions: `ModifyHeader`, `ModifyBody`, `OverrideStatusCode`, `Drop`, `Delay`. Operates on `HttpMessage` at request or response phase.

### Scripted breakpoints

- `CSharpScriptEngine` — Roslyn-based C# script compilation and execution. Script receives the `HttpMessage` and can inspect/modify it.
- `JavaScriptEngine` — Jint-based JS execution with the same contract.

### Replay (`ReplayService`)

Re-sends a captured request with optional modifications; records the new response as a `ReplaySession`. Supports arbitrary header/body overrides.

### Fuzzer (`FuzzerService`)

Mutation-based fuzzer with three strategies:

| Strategy | Description |
|---|---|
| `Random` | Random byte substitutions in the request body |
| `Boundary` | Injects boundary values (empty, max-length, null bytes, format strings) |
| `BitFlip` | Sequential single-bit flips across the body |

Runs mutations concurrently (semaphore-controlled); records each response as a `FuzzerResult`; flags anomalies by comparing response status/size to baseline.

### AI mock engine (`AiMock/`)

Pluggable LLM provider for generating synthetic HTTP responses.

```json
"AiMock": {
  "Provider": "claude",         // claude | openai | ollama
  "Model": "claude-sonnet-4-6",
  "ApiKey": "sk-ant-..."
}
```

Providers: `ClaudeProvider`, `OpenAiProvider`, `OllamaProvider` — all implement `IAiProvider.CompleteAsync(systemPrompt, userPrompt)`.
`AiProviderFactory` selects the provider from config.
`AiMockService` builds a schema from historical captures for the target host, prompts the LLM, parses the structured response, and caches the schema in memory (invalidatable per host).

### Packet analysis (`Analysis/PacketCaptureAnalyzer`)

Implements `IPacketCaptureAnalyzer` — freeze frame, protocol distribution, pattern detection, suspicious activity.

| Detection | Trigger |
|---|---|
| Port scan | 50+ unique destination ports from a single source |
| ARP spoofing | Multiple MAC addresses observed for the same IP |
| DNS anomaly | Long domain queries (>50 chars) or DGA-like patterns |
| Retransmission burst | TCP retransmission rate exceeds 10% |

Thread-safe with lock-based synchronization. Freeze/unfreeze toggles snapshot mode.

### API spec generation (`ApiSpec/`)

Generate OpenAPI 3.0 specs from captured traffic, mock API responses, and replace content in responses.

| Class | Responsibility |
|---|---|
| `ApiSpecGenerator` | Analyzes captured traffic to produce OpenAPI 3.0 JSON; path normalization (GUID/numeric/hex → `{id}`); recursive JSON schema inference with format detection (uuid, date-time, email, uri) |
| `ApiSpecMockService` | Implements `IApiSpecService`; passthrough-first mocking with dual-layer cache (ConcurrentDictionary + Timer-based background DB flush); called from proxy pipeline between rules engine and breakpoints |
| `ContentReplacer` | Content replacement engine: matches by ContentType (wildcards), JsonPath, HeaderValue, BodyRegex; actions: ReplaceWithFile, ReplaceWithUrl, ReplaceWithValue, Redact; file cache with invalidation |
| `ApiSpecLlmEnhancer` | Uses `IAiProvider` to refine specs; truncates to 12k chars for LLM context; validates response structure before accepting |

Registered via `ManipulationExtensions.AddIoTSpyManipulation(aiConfig)`.

---

## IoTSpy.Storage

EF Core 10 data access layer; supports SQLite (default) and PostgreSQL.

### DbSets

| Table | Entity | Notes |
|---|---|---|
| `Captures` | `CapturedRequest` | Indexed on Timestamp, DeviceId, Host |
| `Devices` | `Device` | Unique index on IpAddress |
| `ProxySettings` | `ProxySettings` | Always Id=1 |
| `Certificates` | `CertificateEntry` | Root CA + per-host leaf certs |
| `ScanJobs` | `ScanJob` | Cascade-deletes ScanFindings |
| `ScanFindings` | `ScanFinding` | FK → ScanJob |
| `ManipulationRules` | `ManipulationRule` | Indexed on Enabled + Priority |
| `Breakpoints` | `Breakpoint` | — |
| `ReplaySessions` | `ReplaySession` | — |
| `FuzzerJobs` | `FuzzerJob` | Cascade-deletes FuzzerResults |
| `FuzzerResults` | `FuzzerResult` | FK → FuzzerJob |
| `OpenRtbEvents` | `OpenRtbEvent` | OpenRTB bid request/response events |
| `OpenRtbPiiPolicies` | `OpenRtbPiiPolicy` | PII redaction policies |
| `PiiStrippingLogs` | `PiiStrippingLog` | PII redaction audit trail |
| `CaptureDevices` | `CaptureDevice` | Indexed on IpAddress, MacAddress |
| `Packets` | `CapturedPacket` | FK → CaptureDevice; indexed on Timestamp, DeviceId, Protocol, SourceIp, DestinationIp |
| `ScheduledScans` | `ScheduledScan` | Cron-based recurring scan jobs (Phase 9) |
| `Users` | `User` | Multi-user RBAC identities (Phase 11) |
| `AuditEntries` | `AuditEntry` | Immutable audit log (Phase 11) |
| `DashboardLayouts` | `DashboardLayout` | Per-user saved dashboard configurations (Phase 11) |
| `ApiSpecDocuments` | `ApiSpecDocument` | API spec entities with OpenAPI JSON, status, mock/passthrough flags |
| `ContentReplacementRules` | `ContentReplacementRule` | Content replacement rules with match type, action, priority; FK → ApiSpecDocument |

`DateTimeOffset` columns are stored as Unix milliseconds (`long`) via a `ValueConverter` — required for SQLite `ORDER BY` compatibility.

### Repositories (12+)

`CaptureRepository`, `DeviceRepository`, `CertificateRepository`, `ProxySettingsRepository`, `ScanJobRepository`, `ManipulationRuleRepository`, `BreakpointRepository`, `ReplaySessionRepository`, `FuzzerJobRepository`, `OpenRtbEventRepository`, `OpenRtbPiiPolicyRepository`, `PiiStrippingLogRepository`, `CaptureDeviceRepository`, `ScheduledScanRepository`, `UserRepository`, `AuditRepository`, `DashboardLayoutRepository`, `ApiSpecRepository`

All repositories are **scoped** (one per HTTP request / DI scope) because they depend on the scoped EF Core `DbContext`.

### Migrations (15)

| Migration | Contents | Phase |
|---|---|---|
| `InitialCreate` | Devices, Captures, ProxySettings, CertificateEntries | 1 |
| `AddPhase2ProxySettings` | TransparentProxyPort, TargetDeviceIp, GatewayIp columns | 2 |
| `AddPhase3Scanner` | ScanJobs, ScanFindings | 3 |
| `AddPhase4ManipulationFix` | ManipulationRules, Breakpoints, ReplaySessions, FuzzerJobs, FuzzerResults | 4 |
| `AddOpenRtbInspection` | OpenRtbEvents, OpenRtbPiiPolicies, PiiStrippingLogs | OpenRTB |
| `AddPacketCapture` | CaptureDevices, Packets (with FK and indexes) | 6 |
| `AddMissingPhase7Changes` | Schema fixes for Phase 7 test compatibility | 7 |
| `AddPhase9ScheduledScans` | ScheduledScans table | 9 |
| `AddBodyCaptureDefaults` | Body capture default column values | 9 |
| `AddTlsPassthroughAndSslStrip` | `TlsMetadataJson` (TEXT) on Captures, `SslStrip` (BOOLEAN) on ProxySettings | TLS passthrough |
| `AddPhase11MultiUserAndAudit` | `Users`, `AuditEntries`, `DashboardLayouts` tables | 11 |
| `AddApiSpecAndContentReplacement` | `ApiSpecDocuments`, `ContentReplacementRules` tables with indexes and FK cascade delete | 12 |
| `AddProxyAutoStart` | `AutoStart` (BOOLEAN DEFAULT 0) column on `ProxySettings` | 19 |
| `AddApiKeyManagement` | `ApiKeys` table with unique index on `KeyHash` | 14 |
| `AddPhase15Collaboration` | `InvestigationSessions`, `SessionCaptures`, `CaptureAnnotations`, `SessionActivities` tables | 15 |

---

## IoTSpy.Api

ASP.NET Core 10 host. `Program.cs` wires everything up in order: Storage → Auth → SignalR → Proxy → Scanner → Manipulation → Controllers → CORS → SPA fallback.

### Service lifetimes

- **Singleton**: `ExplicitProxyServer`, `TransparentProxyServer`, `CertificateAuthority`, `ProxyService`, `SslStripService`, `ArpSpoofEngine`, `IptablesHelper`, `SignalRCapturePublisher`, `SignalRAnomalyPublisher`, `PortScanner`, `ServiceFingerprinter`, `CredentialTester`, `ConfigAuditor`, `ScannerService`, `MqttBrokerProxy`, `CoapProxy`, `ApiSpecGenerator`, `ContentReplacer`, `ApiSpecMockService`, `ApiSpecLlmEnhancer`
  *(TCP listeners and other long-lived services that must not be re-created per request)*
- **Hosted services**: `ProxyService` (via `AddHostedService(sp => sp.GetRequiredService<ProxyService>())`), `DataRetentionService` (background cleanup; disabled by default)
- **Scoped**: All EF Core repositories, `DbContext`
- `ProxyService` is registered once as `IProxyService` and once as `IHostedService` via `AddHostedService(sp => sp.GetRequiredService<ProxyService>())` to prevent double instantiation.

### Controllers (16)

| Controller | Endpoints |
|---|---|
| `AuthController` | `POST /api/auth/setup`, `POST /api/auth/login`; admin: user CRUD (`GET/POST/PUT/DELETE /api/auth/users`), `GET /api/auth/audit`; safety guards: cannot self-delete or demote/delete last admin |
| `ProxyController` | `GET/PUT /api/proxy/settings`, `POST /api/proxy/start`, `POST /api/proxy/stop` |
| `CapturesController` | `GET /api/captures` (paged + filtered), `GET /api/captures/{id}`, `DELETE /api/captures/{id}` |
| `DevicesController` | `GET /api/devices`, `GET /api/devices/{id}`, `PUT /api/devices/{id}`, `DELETE /api/devices/{id}` |
| `CertificatesController` | `GET /api/certificates`, `GET /api/certificates/root-ca`, download DER + PEM (no auth), `POST /api/certificates/root-ca/regenerate` (admin: purge all certs + recreate root CA), `DELETE /api/certificates/purge-leaf-certs`, `DELETE /api/certificates/{id}` |
| `AdminController` | Admin-role gated: `GET /api/admin/stats`, `DELETE /api/admin/captures`, `DELETE /api/admin/packets`, `GET /api/admin/export/logs`, `GET /api/admin/export/packets`, `GET /api/admin/export/config` (Phase 20) |
| `ScannerController` | `POST /api/scanner/scan`, `GET /api/scanner/jobs`, `GET /api/scanner/jobs/{id}`, `GET /api/scanner/jobs/{id}/findings`, `POST /api/scanner/jobs/{id}/cancel`, `DELETE /api/scanner/jobs/{id}` |
| `ManipulationController` | CRUD for rules, breakpoints; `POST /replay`, `POST /fuzzer`, `GET+DELETE /fuzzer/{id}`, `POST /ai-mock/generate`, `DELETE /ai-mock/{host}/cache` |
| `PacketCaptureController` | Device list, start/stop capture, packet filtering, freeze frame (create/get), PCAP import with progress streaming, protocol distribution, communication patterns, suspicious activity |
| `OpenRtbController` | OpenRTB events CRUD, PII policies CRUD, PII audit logs |
| `ProtocolProxyController` | MQTT start/stop/status, CoAP start/stop/status |
| `ReportController` | `GET /api/reports/devices/{id}/html`, `GET /api/reports/devices/{id}/pdf` — scan report generation |
| `ScheduledScanController` | CRUD `GET/POST/PUT/DELETE /api/scheduled-scans` — cron-based recurring scan jobs |
| `DashboardController` | Per-user dashboard layout CRUD (`GET/POST/PUT/DELETE /api/dashboard/layouts`) |
| `ApiSpecController` | API spec CRUD, generate from traffic, import/export, LLM refine, activate/deactivate, spec-attached replacement rules CRUD, asset upload/list/delete/content, rule preview (20+ endpoints at `/api/apispec`) |
| `ContentRulesController` | Standalone content replacement rules (no spec required): list with `?host=` filter, create, update, delete, preview (5 endpoints at `/api/contentrules`) |
| `SessionsController` | Investigation sessions CRUD, capture management, annotations, activity feed, AirDrop sharing (Phase 15) |

### SignalR (3 hubs)

- **TrafficHub** (`/hubs/traffic`): Real-time HTTP/HTTPS traffic capture streaming
  - Clients join device groups: `hubConnection.invoke("JoinDeviceGroup", deviceId)`
  - `SignalRCapturePublisher` broadcasts a `TrafficCapture` event to all clients and to the device's group on every captured request
  - Clients subscribe to anomaly alerts: `hubConnection.invoke("SubscribeToAnomalyAlerts")` → receives `AnomalyAlert` events on group `"anomaly-alerts"`; `SignalRAnomalyPublisher` sends alerts emitted by `AnomalyDetector` after each proxied request
  - Extended with WebSocket frame, MQTT message, and anomaly alert subscriptions
- **PacketCaptureHub** (`/hubs/packets`): Live packet capture streaming
  - Clients auto-join `packet-capture-live` group
  - `SignalRPacketPublisher` broadcasts `PacketCaptured` and `CaptureStatus` events for live packet streaming; `PublishImportProgressAsync` broadcasts PCAP import progress (Phase 13)
- **CollaborationHub** (`/hubs/collaboration`): Real-time collaboration (Phase 15)
  - Clients join session groups: `hubConnection.invoke("JoinSession", sessionId)`
  - `AddAnnotation`, `UpdateAnnotation`, `DeleteAnnotation` broadcasts to session group; `PresenceUpdated` and `ActivityCreated` events; viewer-role restrictions enforced on write methods

All hubs accept JWT token via `?access_token=` query parameter (standard SignalR pattern).

### Observability & hardening (Phase 8)

| Feature | Implementation |
|---|---|
| Health checks | `MapHealthChecks("/health")` — liveness + EF Core DB connectivity; `MapHealthChecks("/ready")` — readiness probe. JSON response via `WriteResponse` writer. |
| Structured logging | Serilog with `Console` + rolling `File` sinks (7-day retention). `UseSerilogRequestLogging()` enriches each request log with host and user-agent. Configurable via `Serilog` section. |
| Rate limiting | ASP.NET Core `RateLimiter` middleware. Sliding-window policy partitioned by JWT `sub` (falls back to IP). Default: 100 permits / 60 s. Toggle via `RateLimit:Enabled`. |
| Data retention | `DataRetentionService` (`IHostedService`) runs on configurable interval (default 24 h). Deletes `CapturedRequests`, `CapturedPackets`, `ScanJobs`, and `OpenRtbEvents` older than their configured TTL. **Disabled by default.** |
| Graceful shutdown | Both proxy servers track active connection counts with `Interlocked`. `StopAsync` waits up to a configurable timeout (default 10 s) for in-flight connections to drain. |
| DB connection pooling | `StorageExtensions.AddIoTSpyStorage()` accepts `maxPoolSize` / `minPoolSize`; Postgres connection strings are auto-augmented with `Maximum Pool Size` / `Minimum Pool Size`. |

### Authentication

- Multi-user RBAC model (Phase 11): `User` table with `UserRole` enum (Admin/Operator/Viewer); PBKDF2-SHA256 password hashing; JWT claims include `NameIdentifier` (user ID), `Name` (username), and `Role`
- Backward-compatible with legacy single-user auth via `ProxySettings.PasswordHash` (used when no `User` record matches)
- Admin-only endpoints: user CRUD and audit log in `AuthController`; all `AdminController` endpoints; cert regenerate in `CertificatesController`; SignalR accepts token via `?access_token=` query param
- Safety guards: cannot delete own account; cannot delete or demote the last admin (both `DeleteUser` and `UpdateUser` enforce this)
- Login response includes `{ token, user: { id, username, displayName, role } }`; frontend persists `user` in `localStorage["iotspy-user"]` and exposes it via `useCurrentUser()` hook for role-gated UI
- JWT issuer and audience: `"iotspy"`
- `Auth:JwtSecret` must be ≥ 32 characters; app throws on startup if absent
- OpenAPI (Scalar) at `/scalar` in Development mode only

---

## Data flow

```
IoT Device → ExplicitProxy :8888      (explicit mode — device configured to use proxy)
           → TransparentProxy :9999   (gateway/ARP mode — iptables REDIRECT)
           → MqttBrokerProxy :1883    (MQTT MITM — decodes packets, topic-level filtering)
           → CoapProxy :5683 (UDP)    (CoAP forward proxy — decodes messages, captures)
  └─ ExplicitProxyServer / TransparentProxyServer
       ├─ Plain HTTP → InterceptHttpStreamAsync
       ├─ WebSocket Upgrade (101) → RelayWebSocketFramesAsync (bidirectional frame capture)
       ├─ gRPC (application/grpc) → capture with Protocol=Grpc
       ├─ Plain HTTP + SslStrip=true → SslStripService (intercept HTTPS redirects, fetch upstream TLS, serve HTTP)
       └─ TLS (CONNECT or transparent)
            ├─ CaptureTls=false → HandleTlsPassthroughAsync
            │    └─ Parse ClientHello (SNI, JA3) → relay → parse ServerHello (JA3S) + Certificate (TLS 1.2)
            │         → record CapturedRequest with Protocol=TlsPassthrough + TlsMetadataJson
            └─ CaptureTls=true  → CertificateAuthority.GetOrCreateHostCertAsync()
                                   SslStream MITM → InterceptHttpStreamAsync
                                        ├─ IManipulationService (rules → scripts, request + response phases)
                                        ├─ CaptureRepository (SQLite/PG)
                                        └─ ICapturePublisher → SignalR (group-routed) → dashboard
```

---

## Frontend (Vite 6 + React 19 + TypeScript)

Located in `frontend/`. Dev server on `:3000`; Vite proxies `/api` and `/hubs` to `http://localhost:5000`.

### Auth flow

`GET /api/auth/status` on mount → `/setup` if no password set → `/login` if no token → dashboard.
JWT stored in `localStorage['iotspy_token']`; passed as `?access_token=` for SignalR.
Login response writes `user: { id, username, displayName, role }` to `localStorage['iotspy-user']`; cleared on logout.
`useCurrentUser()` reads this for role-gated UI (admin link in header, `/admin` route guard).

### Layout & panels

| Panel | Components | Features |
|---|---|---|
| Captures | Split-pane list + detail (request / response / TLS tabs) | `CaptureFilterBar`; `BodyViewer` with three modes (Pretty/Raw/Hex); SSE/NDJSON stream rendering with collapsible per-event rows |
| Devices | Device list with timeline swimlane view per device | Real-time sync via SignalR; swimlane labels resizable and ellipsis-truncated |
| Scanner | `ScannerPanel` → `ScanJobList` + `ScanFindingsView` | Scan job progress tracking; detailed findings with severity classification |
| Manipulation | `ManipulationPanel` → `RulesEditor` (tab: "Traffic Rules"), `BreakpointsEditor`, `ReplayPanel`, `FuzzerPanel`, `ContentRulesPanel`, `AssetLibrary`, `ApiSpecPanel`, `GrpcSchemasPanel` | Traffic Rules: header/body/status/delay/drop manipulation; Content Rules: binary-safe content replacement + SSE stream mocking, live host filter; Assets: upload/manage replacement files; API Spec: documentation-only (generate/import/export/refine); gRPC Schemas: upload `.proto` files for field-name decoding |
| Packet Capture | `PanelPacketCapture` (tabbed: Packets / Protocols / Patterns / Suspicious) | `PacketListFilterable`, `PacketInspector` (Details / Hex Dump / Layers), `ProtocolDistributionView`, `PatternExplorer`, `SuspiciousActivityPanel`; drag-drop PCAP import with progress bar |
| Live stream | `useTrafficStream` via SignalR | New captures prepended in real time with teal glow animation; alternating row stripes; HTTP method + status color coding |
| Sessions (Phase 15) | `SessionsPanel` → list/create/detail views | `AnnotationPanel` for per-capture notes + tags; `PresenceIndicator` with avatar chips; session export (ZIP); AirDrop sharing |
| Admin (`/admin`, Phase 20) | `AdminPage` — role-guarded (`admin` only) | Five management tabs: **Database** (stats, purge, export), **Certificates** (CA metadata, DER/PEM download, regenerate), **Audit** (paginated log), **Users** (CRUD with safety guards), **Plugins** (list + admin-only reload) |

### BodyViewer (Phase 19–20)

`BodyViewer` component (used in capture detail and packet inspector) provides three view modes and stream-aware rendering:

**View modes:**
- **Pretty** — Content-type-aware rendering: JSON with syntax highlighting, XML/HTML with formatting, images rendered as `<img>` via Blob URL, automatic decompression (gzip/deflate/Brotli) with indicator
- **Raw** — Plain text view with copy-to-clipboard support
- **Hex** — Wireshark-style offset/hex/ASCII dump (16 bytes per row, capped at 8 KiB) with line-by-line byte breakdown

**Stream rendering (Pretty mode):**
- `detectStream(body, contentType)` → `{ kind: 'sse' | 'ndjson'; events: StreamEvent[] } | null`
  - `text/event-stream` → `parseSSE()`: splits on double-newline, extracts `data:` / `event:` / `id:` / `retry:` fields
  - `application/x-ndjson`, `application/jsonl`, or sniffed (every line parses as JSON) → `parseNDJSON()`
- `StreamEventRow` — collapsible per-event row: chevron + index + label + byte count; optional SSE metadata; JSON syntax-highlighted body or plain text
- Toolbar: event count badge; Expand all / Collapse all toggle
- Info toolbar shows Content-Type, Content-Encoding (e.g. `gzip ✓ decoded`), byte size

### Source structure

```
frontend/src/
  types/api.ts              # TS interfaces matching all backend DTOs
  api/
    apiFetch.ts             # fetch wrapper (auth header, error handling)
    captures.ts
    devices.ts
    proxy.ts
    scanner.ts
    manipulation.ts
    packetCapture.ts          # Packet capture + analysis endpoints
  store/authStore.tsx        # React Context + useReducer
  hooks/
    useAuth.ts
    useCaptures.ts
    useDevices.ts
    useProxy.ts
    useTrafficStream.ts      # SignalR live stream
    useScanner.ts
    useManipulation.ts
    usePacketCapture.ts      # SignalR packet stream + capture lifecycle
    usePacketAnalysis.ts     # Protocol distribution, patterns, suspicious activity
    useFocusTrap.ts          # Tab/Shift+Tab trap for modals; auto-focuses first element
  pages/                     # SetupPage, LoginPage, DashboardPage
  components/                # layout, captures, capture-detail, proxy, devices,
                             # scanner, manipulation, common
  styles/                    # CSS custom properties + per-component sheets
```

### Frontend design (Phase 18.5)

**Typography & branding:**
- Primary font: **Space Grotesk** (headings, bold text)
- Secondary font: **IBM Plex Sans** (body text, fallback)
- Accent color: **Teal** (`#00c9b1`) replacing generic blue (`#4e7aff`)
- Logo: Radar/signal SVG icon (two concentric arcs + dot) replacing placeholder 'I'

**UI components & styling:**
- Start/Stop buttons: Solid filled (green/red with white text) with subtle depth shadow
- Capture rows: 40px min-height; alternating row stripes; 4xx/5xx left-border color coding; real-time flash animation (1.4s teal glow on new captures)
- Badges: 2px 5px padding (increased from 1px); DELETE badge solid red + white text; letter-spacing for readability
- Empty states: Inline SVGs (magnifying glass, document outline) replacing emoji icons
- Border radius tokens: `sm` 5px (4→5), `lg` 10px (8→10)
- Scrollbars: 4px width (6→4), transparent track, semi-transparent thumb
- Focus rings: Global `:focus-visible` with teal outline
- Modal accessibility: `useFocusTrap` hook (Tab/Shift+Tab cycles within modal, auto-focuses first element); `role="dialog"` / `aria-modal` / `aria-labelledby` on all modal inner divs; Escape handler via stable `useRef` pattern
- ARIA tab semantics: `ManipulationPanel`, `PanelPacketCapture`, `PacketInspector` all use `role="tablist"` / `role="tab"` / `aria-selected` / `role="tabpanel"`
- Capture list keyboard access: `RulePreviewModal` capture rows have `role="listbox"` / `role="option"` / `tabIndex={0}` / `onKeyDown` (Enter/Space)
- CSS migrations: inline `style={{}}` with hex literals extracted to component-scoped stylesheets (`content-rules.css`, `asset-library.css`, `packet-inspector.css`); all colors use `var(--color-*)` tokens

**Responsive design:**
- Breakpoints: 480px (mobile), 768px (tablet), 1024px (desktop)
- Stacked split panes on mobile; scrollable view toggles
- Timeline swimlane labels resizable (100–320 px range)

**Dark/light theme:**
- CSS custom properties with `[data-theme="dark"|"light"]` attribute
- `useTheme` hook; persisted in localStorage
- Theme toggle in header

---

## Test projects

**1022 backend tests** across 9 test projects + 125 frontend component tests. All passing. Coverage reported via Coverlet + ReportGenerator in CI. Test coverage includes Phase 10 decoders, Phase 11 multi-user/TLS/tests, Phase 12 API spec generation, Phase 14 API keys, Phase 15 collaboration, Phase 20 admin/integration tests, Gaps Batch 5 (CoAP Block-wise/Observe, DNS EDNS0, WebSocket sub-protocol detection, MQTT topic statistics, rule cache), Gaps Batch 6 (gRPC schema upload, audit write-once trigger), the modal-system pass (RulePreviewModal accessibility + focus trap), scan-scope/consent-gate (CIDR enforcement, scope CRUD, CancellationToken hardening across all test files), and plugin-signing/audit-retention (PluginSignatureVerifier trust status + RSA/ECDSA crypto path coverage, DataRetentionService audit archiving).

| Project | Test classes | Coverage |
|---|---|---|
| `IoTSpy.Core.Tests` | `ModelDefaultTests`, `EnumCoverageTests`, `UserModelTests`, `AuditEntryTests` | Model defaults/enum coverage; User/AuditEntry/DashboardLayout validation |
| `IoTSpy.Protocols.Tests` | `MqttDecoderTests`, `DnsDecoderTests`, `TelemetryDecoderTests`, `AnomalyDetectorTests`, `WebSocketDecoderTests`, `GrpcDecoderTests`, `ModbusDecoderTests`, `CoapDecoderTests`, `MqttTopicMatchTests`, `MqttSessionAnalyzerTests` | MQTT 3.1.1/5.0 + topic stats + QoS-2 flow tracking; DNS/mDNS + EDNS0 OPT; all four telemetry decoders; anomaly detection; WebSocket RFC 6455 frames + sub-protocol detection; gRPC LPM; Modbus TCP; CoAP RFC 7252 + Block-wise/Observe; MQTT topic wildcard matching |
| `IoTSpy.Manipulation.Tests` | `RulesEngineTests`, `FuzzerServiceTests`, `OpenRtbPiiServiceTests`, `ApiSpecGeneratorTests`, `ContentReplacerTests`, `ManipulationRuleCacheTests` | Rule matching + all actions; fuzzer mutation strategies; OpenRTB PII detection + redaction; API spec path normalization + JSON schema inference; content replacement match types + actions; manipulation rule cache invalidation |
| `IoTSpy.Scanner.Tests` | `PortScannerTests`, `PacketCaptureServiceTests`, `CidrHelperTests` | Concurrency limiting, timeout handling; ring buffer, PCAP export; IPv4/IPv6 CIDR containment + edge cases |
| `IoTSpy.Api.Tests` | `AuthControllerTests`, `ProxyControllerTests`, `CapturesControllerTests`, `DevicesControllerTests`, `ScannerControllerTests`, `ScanScopeControllerTests`, `AuthServiceTests` | Controller unit tests with NSubstitute mocks; multi-user + legacy auth; `AuthService` PBKDF2/JWT logic; scan scope CRUD + consent gate enforcement |
| `IoTSpy.Proxy.Tests` | `ProxyServiceTests`, `ResilienceOptionsTests`, `GracefulShutdownTests`, `TlsClientHelloParserTests`, `TlsServerHelloParserTests`, `SslStripServiceTests` | ProxyService state machine; resilience defaults; graceful shutdown; TLS handshake parsing (JA3/JA3S/SNI); SSL strip redirect/HSTS/rewrite |
| `IoTSpy.Storage.Tests` | `DeviceRepositoryTests`, `CaptureRepositoryTests`, `ProxySettingsRepositoryTests`, `DataRetentionServiceTests`, `ScanScopeRepositoryTests` | EF Core in-memory SQLite integration tests; data retention service cleanup; scan scope persistence + active filtering |
| `IoTSpy.Api.IntegrationTests` | `AuthIntegrationTests`, `DevicesIntegrationTests`, `HealthCheckEndpointTests`, `AdminControllerTests`, `CertificatesControllerTests`, `UserSafetyGuardsTests` | `WebApplicationFactory` with NSubstitute fakes; full HTTP auth flow; health check endpoint contract; admin stats/purge/export; cert regenerate; self-delete + last-admin guards |

---

## Configuration reference

```jsonc
// src/IoTSpy.Api/appsettings.json
{
  "Database": {
    "Provider": "sqlite",            // sqlite | postgres
    "ConnectionString": "Data Source=iotspy.db"
  },
  "Auth": {
    "JwtSecret": "<≥32 chars>",      // REQUIRED — also via Auth__JwtSecret env var
    "PasswordHash": ""               // populated by POST /api/auth/setup
  },
  "Frontend": {
    "Origin": "http://localhost:3000"  // CORS allowed origin
  },
  "Urls": "http://localhost:5000",
  "Resilience": {
    "ConnectTimeoutSeconds": 15,
    "TlsHandshakeTimeoutSeconds": 10,
    "RetryCount": 2,
    "RetryBaseDelaySeconds": 0.5,
    "CircuitBreakerFailureRatio": 0.5,
    "CircuitBreakerSamplingSeconds": 30,
    "CircuitBreakerBreakSeconds": 60
  },
  "AiMock": {
    "Provider": "claude",            // claude | openai | ollama
    "Model": "claude-sonnet-4-6",
    "ApiKey": "",
    "BaseUrl": ""                    // Ollama only
  },
  "Serilog": {
    "MinimumLevel": "Information"    // overridable per namespace
  },
  "RateLimit": {
    "Enabled": true,
    "PermitLimit": 100,
    "WindowSeconds": 60
  },
  "DataRetention": {
    "Enabled": false,                // opt-in; set true to enable automatic cleanup
    "IntervalHours": 24,
    "CaptureRetentionDays": 30,
    "PacketRetentionDays": 7,
    "ScanJobRetentionDays": 90,
    "OpenRtbEventRetentionDays": 14
  }
}
```

Environment variable overrides use double-underscore: `Auth__JwtSecret=...`

---

## Extending the system

### Adding a protocol decoder

1. Create a class in `IoTSpy.Protocols/<Protocol>/` implementing `IProtocolDecoder<YourMessage>`.
2. Add the decoder as a singleton in `Program.cs` (or a dedicated extensions class).
3. Add tests to `IoTSpy.Protocols.Tests`.

### Adding a manipulation action

1. Add a value to `ManipulationRuleAction` in `IoTSpy.Core`.
2. Implement the action branch in `RulesEngine.ApplyAsync`.
3. Add the corresponding `ManipulationController` endpoint if it needs a dedicated API.

### Switching AI provider

Update `AiMock:Provider` in `appsettings.json` to `claude`, `openai`, or `ollama`. No code changes required. For Ollama, set `AiMock:BaseUrl` to the local Ollama API address (e.g. `http://localhost:11434`).

### Switching database

Set `Database:Provider` to `postgres` and provide a Postgres connection string in `Database:ConnectionString`. Run `dotnet ef database update` to apply migrations.
