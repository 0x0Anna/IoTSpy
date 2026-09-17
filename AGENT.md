# AGENT.md

Guidance for AI agents working with the IoTSpy codebase.

## What this project is

IoTSpy is an IoT network security research platform: transparent MITM proxy, multi-protocol analyzer, pen-test suite, and traffic manipulation engine. Backend is .NET 10 / ASP.NET Core. Frontend is Vite 6 + React 19 + TypeScript. Storage is SQLite (default) or PostgreSQL via EF Core.

## Commands

```bash
# Build entire solution
dotnet build

# Run all tests
dotnet test

# Run a single test project
dotnet test src/IoTSpy.SomeTests/IoTSpy.SomeTests.csproj

# Restore dependencies
dotnet restore

# Run the API — requires Auth:JwtSecret in user-secrets (see Dev secrets section)
dotnet run --project src/IoTSpy.Api

# Add EF Core migration (run from repo root)
dotnet ef migrations add <MigrationName> --project src/IoTSpy.Storage --startup-project src/IoTSpy.Api

# Apply migrations manually
dotnet ef database update --project src/IoTSpy.Storage --startup-project src/IoTSpy.Api

# Frontend
cd frontend && npm install && npm run dev
cd frontend && npm test        # Vitest
cd frontend && npm run build
```

Scalar API docs: `http://localhost:5000/scalar` (Development mode only).

## Project layout

```
IoTSpy.sln
src/
  IoTSpy.Api/                  ASP.NET Core host — controllers, SignalR hubs, middleware
  IoTSpy.Core/                 Domain models, interfaces, enums — no infrastructure deps
  IoTSpy.Proxy/                TCP listener, TLS MITM/passthrough, SSL stripping, Polly resilience
  IoTSpy.Storage/              EF Core DbContext + repositories (SQLite/Postgres)
  IoTSpy.Protocols/            MQTT, MQTT-SN, DNS, CoAP, WebSocket, gRPC, Modbus, OpenRTB, RTSP/RTP, AMQP 1.0, DoH detection, telemetry decoders
  IoTSpy.Scanner/              Port scan, service fingerprinting, CVE lookup, packet capture
  IoTSpy.Manipulation/         Rules engine, scripted breakpoints, replay, fuzzer, AI mock, OpenRTB PII, API spec generation, content replacement
  IoTSpy.*.Tests/              Unit + integration tests (NSubstitute mocks, EF Core in-memory)
  IoTSpy.Api.IntegrationTests/ WebApplicationFactory integration tests
frontend/                      Vite + React + TypeScript dashboard
docs/
  ARCHITECTURE.md              Full architecture spec
  PLAN-INDEX.md                Navigation hub; links to PHASES-*.md and CODE-REVIEW-FINDINGS.md (the live backlog board)
```

### Dependency graph

```
IoTSpy.Api
  ├── IoTSpy.Core
  ├── IoTSpy.Proxy         → IoTSpy.Core, IoTSpy.Protocols
  ├── IoTSpy.Storage       → IoTSpy.Core
  ├── IoTSpy.Protocols     → IoTSpy.Core
  ├── IoTSpy.Scanner       → IoTSpy.Core
  └── IoTSpy.Manipulation  → IoTSpy.Core, IoTSpy.Protocols
```

`IoTSpy.Core` has zero infrastructure dependencies — keep it that way.

## Architecture essentials

### Service lifetimes

- **Singleton**: `ExplicitProxyServer`, `TransparentProxyServer`, `CertificateAuthority`, `ProxyService`, `SslStripService`, `MqttBrokerProxy`, `CoapProxy`, `PortScanner`, `ScannerService`, all SignalR publishers.
- **Scoped**: all EF Core repositories.
- `ProxyService` is registered as both `IProxyService` (singleton) and `IHostedService` via `AddHostedService(sp => ...)` — do not register it twice.

### Adding a new feature

1. **Models/interfaces** go in `IoTSpy.Core`.
2. **EF entities + repositories** go in `IoTSpy.Storage`; add a migration.
3. **Protocol decoding** goes in `IoTSpy.Protocols`.
4. **HTTP/proxy-layer logic** goes in `IoTSpy.Proxy`.
5. **REST endpoints** go in a controller in `IoTSpy.Api`.
6. **Tests** mirror the project being tested (e.g. `IoTSpy.Protocols.Tests`).

### Storage

Switch between SQLite and Postgres via `appsettings.json`:

```json
"Database": { "Provider": "Sqlite" }   // or "Postgres"
"ConnectionStrings": { "DefaultConnection": "..." }
```

`MigrateAsync()` runs at startup automatically. Use `DesignTimeDbContextFactory` when running `dotnet ef` CLI commands.

### Dev secrets (one-time setup)

`Auth:JwtSecret` is required at startup (≥ 32 chars) and must never be stored in source. Use the .NET user-secrets store:

```bash
dotnet user-secrets set "Auth:JwtSecret" "your-32-char-minimum-dev-secret-here" --project src/IoTSpy.Api
```

User secrets are loaded automatically when `ASPNETCORE_ENVIRONMENT=Development`. The VS Code launch config (`launch.json`) sets this env var and also pins `ASPNETCORE_URLS=http://localhost:5000` so the Vite dev proxy always reaches the correct port. Do not add `Auth:JwtSecret` to `launch.json` or `appsettings.json`.

### Authentication

JWT bearer auth. `Auth:JwtSecret` must be ≥ 32 characters (throws on startup if absent). Multi-user RBAC: `UserRole` enum (Admin / Operator / Viewer). SignalR accepts the token via `?access_token=` query param.

### Real-time streaming

SignalR hubs:
- `TrafficHub` (`/hubs/traffic`) — captured HTTP/HTTPS, WebSocket frames, MQTT messages, anomaly alerts
- `PacketCaptureHub` (`/hubs/packets`) — raw packet stream

## Naming conventions

- Namespace prefix: `IoTSpy` (capital **I**, lowercase **o**, capital **T**, capital **S**)
- Docker image / container: `iotspy` (all lowercase)
- Test class naming: `<ClassName>Tests` in the matching `IoTSpy.*.Tests` project
- C# style: standard .NET conventions; async methods suffixed with `Async`

## Testing guidance

- Run `dotnet test` before committing; all backend tests must pass.
- New backend code needs corresponding tests. Prefer unit tests with NSubstitute mocks; use EF Core SQLite in-memory for repository tests.
- Frontend tests use Vitest + React Testing Library (`npm test` inside `frontend/`).
- CI runs on every push/PR via `.github/workflows/ci.yml`.

### CancellationToken in xUnit tests (xUnit1051)

**Never pass `CancellationToken.None` to async methods in xUnit tests.** Use `TestContext.Current.CancellationToken` instead. This lets xUnit cancel the test immediately when the test run is aborted, rather than waiting for the operation to time out.

```csharp
// Wrong — triggers xUnit1051 warning
var result = await controller.ListJobs(1, 20, ct: CancellationToken.None);

// Correct
var result = await controller.ListJobs(1, 20, ct: TestContext.Current.CancellationToken);
```

This applies everywhere a `CancellationToken` parameter is accepted — controller calls, repository calls, `Task.Delay`, `StartAsync`/`StopAsync`, etc. The `TestContext` static is available in all xUnit test classes without any extra imports.

## Key configuration sections (`appsettings.json`)

| Section | Purpose |
|---|---|
| `Auth:JwtSecret` | Required; ≥ 32 chars |
| `Database:Provider` | `Sqlite` (default) or `Postgres` |
| `Resilience` | Polly pipeline defaults (timeout, retry, circuit-breaker) |
| `RateLimit:Enabled` | Sliding-window rate limiter toggle |
| `DataRetention:Enabled` | Background TTL cleanup (default: false) |
| `Serilog` | Log sinks and minimum level |

## Available Claude Code skills

Project-specific skills live in `.claude/skills/` and are auto-discovered by Claude Code — no install step required.

| Skill | When to use |
|---|---|
| `/dotnet-engineer` | ASP.NET Core, EF Core, SignalR, Polly, xUnit/NSubstitute architecture guidance (project-agnostic) |
| `/security-code-review` | Systematic security review across input handling, authz, resources, errors, crypto, secrets, and supply chain |
| `/threat-modeling` | Structured threat modeling — STRIDE + OWASP + ATT&CK with calibrated severity and dual-use tool considerations |
| `/iotspy-context` | IoTSpy-specific architecture, conventions, and security caveats — pair with any of the above when working in this repo |

See `.claude/skills/README.md` for full details.

### Vite dev proxy

`frontend/vite.config.ts` proxies `/api` and `/hubs` to `http://localhost:5000`. Both routes have custom error handlers that suppress `ECONNRESET` (expected on backend restart — SignalR reconnects automatically) and log all other errors. If you see `ECONNREFUSED` in the Vite console, the backend is not running or not bound to port 5000.

### Proxy resilience pipeline

`IoTSpy.Proxy` uses Polly v8 for outbound connection resilience (timeout → retry → circuit breaker). The circuit breaker is **per upstream hostname** via `PerHostConnectPipelineCache` (`src/IoTSpy.Proxy/Resilience/PerHostConnectPipelineCache.cs`) — a singleton that lazily creates and caches one `ResiliencePipeline` per host. This ensures a dead or unresolvable IoT endpoint only opens its own circuit and never blocks traffic to other hosts. The TLS handshake pipeline (`iotspy-tls`) is a single shared timeout-only pipeline registered via `AddResiliencePipeline` and retrieved by its fixed key.

## Known operational requirements

### Packet capture on Linux — setcap

SharpPcap requires `CAP_NET_RAW` and `CAP_NET_ADMIN` to open raw sockets. On Linux, grant them to the **real** dotnet binary (not the symlink — `setcap` refuses symlinks):

```bash
sudo setcap cap_net_raw,cap_net_admin+eip "$(readlink -f $(which dotnet))"
# typically resolves to: /usr/share/dotnet/dotnet
```

If you see "Could not find any devices" in the Packet Capture tab, this is the most likely cause. Restart the API after running `setcap`.

### JSON enum serialization

`Program.cs` configures **both** MVC controllers and the SignalR JSON hub protocol with `JsonStringEnumConverter`:

```csharp
builder.Services.AddControllers()
    .AddJsonOptions(opts => opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR()
    .AddJsonProtocol(opts => opts.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
```

This ensures `InterceptionProtocol` serializes as `"Http"`, `"Mqtt"`, etc. (not `0`, `1`, …) across both REST API responses and SignalR events. If you remove or forget the `AddJsonProtocol` call, live-streamed captures will have numeric protocols and the frontend timeline will crash.

### TLS certificate requirements for iOS/macOS

The `CertificateAuthority` class generates leaf certs meeting Apple's requirements:

- **Validity ≤ 397 days** — Apple enforces a 398-day cap on TLS leaf certificates (policy effective Sep 2020). Certs exceeding this are silently rejected by iOS/macOS even if the root CA is trusted.
- **AKI keyid-only form** — iOS 16+ (and iOS 26) rejects the full Authority Key Identifier form (`keyId + DirName + serial`) emitted by `CreateAuthorityKeyIdentifier(cert)`. Use `CreateAuthorityKeyIdentifier(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(caKeyPair.Public))` (keyid-only, matching mitmproxy/Charles/Proxyman behaviour).
- **SAN with IP literal** — For IP address hostnames, the SAN must use `GeneralName.IPAddress` with `DerOctetString(ip.GetAddressBytes())`, not `DnsName`. iOS rejects DnsName-as-IP.

If you regenerate the root CA or change cert generation logic, **delete all leaf certs** from the database so they are re-generated with the corrected extensions:
```bash
sqlite3 src/IoTSpy.Api/iotspy.db "DELETE FROM Certificates WHERE IsRootCa = 0;"
```

### EF Core migrations on SQLite

SQLite migrations that call `AlterColumn` generate `PRAGMA foreign_keys = 0` statements, which **cannot execute inside a transaction**. EF Core wraps migrations in transactions, so the migration will fail with:

> `The migration operation 'PRAGMA foreign_keys = 0;' from migration 'X' cannot be executed in a transaction`

**Workaround:** Replace `AlterColumn` with direct `migrationBuilder.Sql(...)` calls (e.g. `UPDATE` statements to backfill defaults). See `20260322032005_AddBodyCaptureDefaults.cs` for an example.

Every migration must have a matching `.Designer.cs` file with the `[Migration("...")]` attribute. Without it EF Core never discovers the migration. The Designer file can have a stub `BuildTargetModel` body — the full model is in `IoTSpyDbContextModelSnapshot.cs`.

## Quick workflows

### Add a new controller endpoint

1. Define models in `IoTSpy.Core/Models/`
2. Define interface in `IoTSpy.Core/Interfaces/`
3. Implement repository in `IoTSpy.Storage/Repositories/`
4. Implement controller in `IoTSpy.Api/Controllers/`
5. Add unit tests in `IoTSpy.Api.Tests/Controllers/`
6. Run `dotnet test` to verify all tests pass

### Add a new protocol decoder

1. Define `IProtocolDecoder` in `IoTSpy.Core/Interfaces/`
2. Implement decoder in `IoTSpy.Protocols/Decoders/`
3. Register in `IoTSpy.Api/Program.cs` (if needed)
4. Add tests in `IoTSpy.Protocols.Tests/`

### Add a rule or manipulation feature

1. Define models + interfaces in `IoTSpy.Core/`
2. Implement rules engine logic in `IoTSpy.Manipulation/`
3. Add EF entity + migration in `IoTSpy.Storage/`
4. Expose via REST endpoint in `IoTSpy.Api/Controllers/ManipulationController.cs`
5. Test with `IoTSpy.Manipulation.Tests/`

## Before committing

- [ ] All backend tests pass: `dotnet test`
- [ ] Frontend tests pass: `cd frontend && npm test`
- [ ] New backend code has unit tests
- [ ] No infrastructure dependencies added to `IoTSpy.Core`
- [ ] New EF entities have a migration (`dotnet ef migrations add ...`)
- [ ] SignalR changes include `JsonStringEnumConverter` on both controllers and SignalR hub

## What to avoid

- Do not add infrastructure dependencies to `IoTSpy.Core`.
- Do not register a singleton service as both a singleton and a hosted service separately — use the `AddHostedService(sp => sp.GetRequiredService<T>())` pattern.
- Do not disable upstream TLS validation outside of test/development code (it is intentionally disabled in the proxy for research purposes — do not spread this pattern).
- Do not store secrets in source code; use environment variables or user-secrets.
