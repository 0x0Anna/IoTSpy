# CLAUDE.md

Guidance for Claude Code when working in this repository. See [AGENT.md](AGENT.md) for the full technical reference (commands, architecture, naming conventions, testing guidance).

## Quick commands

```bash
# Build
dotnet build

# Test (all / single project)
dotnet test
dotnet test src/IoTSpy.SomeTests/IoTSpy.SomeTests.csproj

# Run the API — set the JWT secret via user-secrets first (one-time, see Dev secrets below)
dotnet run --project src/IoTSpy.Api

# Add / apply EF Core migration (run from repo root)
dotnet ef migrations add <MigrationName> --project src/IoTSpy.Storage --startup-project src/IoTSpy.Api
dotnet ef database update --project src/IoTSpy.Storage --startup-project src/IoTSpy.Api

# Frontend
cd frontend && npm install && npm run dev
cd frontend && npm test && npm run build
```

Scalar API docs: `http://localhost:5000/scalar` (Development only).

## Dev secrets (one-time setup)

`Auth:JwtSecret` is required at startup (≥ 32 chars). Store it in the .NET user-secrets store — never in source:

```bash
dotnet user-secrets set "Auth:JwtSecret" "your-32-char-minimum-dev-secret-here" --project src/IoTSpy.Api
```

User secrets are loaded automatically when `ASPNETCORE_ENVIRONMENT=Development` (the VS Code launch config sets this). The VS Code launch config (`launch.json`) pins `ASPNETCORE_URLS=http://localhost:5000` so the Vite proxy target always matches.

## Project structure at a glance

```
IoTSpy.Api          ASP.NET Core host — controllers, SignalR hubs, middleware
IoTSpy.Core         Domain models, interfaces, enums (no infrastructure deps)
IoTSpy.Proxy        TLS MITM/passthrough, SSL stripping, WebSocket/MQTT/CoAP proxies
IoTSpy.Storage      EF Core DbContext + repositories (SQLite/Postgres)
IoTSpy.Protocols    MQTT, DNS, CoAP, WebSocket, gRPC, Modbus, OpenRTB, telemetry decoders
IoTSpy.Scanner      Port scan, fingerprinting, CVE lookup, packet capture
IoTSpy.Manipulation Rules engine, scripted breakpoints, replay, fuzzer, AI mock, OpenRTB PII, API spec generation, content replacement
IoTSpy.*.Tests      Unit + integration tests
frontend/           Vite 6 + React 19 + TypeScript dashboard
docs/               ARCHITECTURE.md, PLAN-INDEX.md
```

Controllers under `IoTSpy.Api/Controllers/` (20): Admin, ApiSpec, Auth, Captures, Certificates, ContentRules, Dashboard, Devices, Manipulation, OpenRtb, PacketCapture, PassiveCapture, Plugins, ProtocolProxy, ProtoSchemas, Proxy, Report, Scanner, ScheduledScan, Sessions.

## Available skills

Project-specific Claude Code skills live in `.claude/skills/` and are auto-discovered — no install step required.

| Skill | When to use |
|---|---|
| `/dotnet-engineer` | ASP.NET Core, EF Core, SignalR, Polly, xUnit/NSubstitute architecture guidance (project-agnostic) |
| `/security-code-review` | Systematic security review across input handling, authz, resources, errors, crypto, secrets, and supply chain |
| `/threat-modeling` | Structured threat modeling with calibrated severity and dual-use tool considerations |
| `/iotspy-context` | IoTSpy-specific architecture, conventions, and security caveats — pair with the above when in this repo |

See `.claude/skills/README.md` for full details.

## Workflow rules

- **Always run `dotnet test` before committing.** All backend tests must stay green.
- New backend logic needs a corresponding test. Use NSubstitute for mocks; use EF Core SQLite in-memory for repository tests.
- New EF entities require a migration (`dotnet ef migrations add ...`).
- Keep `IoTSpy.Core` free of infrastructure dependencies.
- Use the `AddHostedService(sp => sp.GetRequiredService<T>())` pattern for services that are both singleton and hosted — never double-register.
- Namespace prefix is `IoTSpy` (capital I, o, T, S). Docker image/container name is `iotspy`.
- **Do not add `Co-Authored-By: Claude` trailers** to commit messages or PR descriptions. Keep attribution noise out of the git history.

## Current state

All phases 1–16, 18–22 plus API & Backend Polish, Frontend Usability enhancements, Gaps Batches 4, 5, and 6 are complete:
- 917 backend `[Fact]`/`[Theory]` attributes across 9 test projects (989 executed test cases); 102 frontend component tests; Playwright E2E suite (auth, captures, dashboard, manipulation)
- 22 REST controllers, 208 endpoints
- 27 EF Core migrations up through `AddScheduledScanLastRunStatus`
- GitHub Actions CI at `.github/workflows/ci.yml`
- Helm chart at `deploy/helm/iotspy/`; production Docker Compose at `docker-compose.prod.yml`

> Counts above last verified 2026-09-17. To re-check: `grep -rE "^\s*\[(Fact|Theory)" --include="*.cs" src/IoTSpy.*.Tests src/IoTSpy.Api.IntegrationTests | wc -l`, `ls src/IoTSpy.Api/Controllers | wc -l`, `ls src/IoTSpy.Storage/Migrations/*.cs | grep -vE "(Designer|Snapshot)" | wc -l`, `grep -rE "\[Http" --include="*.cs" src/IoTSpy.Api/Controllers | wc -l`.

### feature/config-export-import-polish (latest)
`docs/CODE-REVIEW-FINDINGS.md` items #29, #30, #31, #32, #34, #35 (the "incomplete-feature polish" PR):
- HAR export headers (#29): `IoTSpy.Core.Utilities.HttpHeaderParser` parses the raw `Name: Value\r\n...` header text actually stored in `CapturedRequest.RequestHeaders`/`ResponseHeaders` (despite their "JSON-serialized" doc comment); `CapturesController.BuildHar` and `ParseContentType` both now use it instead of the previous silently-broken `JsonDocument.Parse` assumption
- Config export/import round-trip (#30, #31): `AdminController.ExportConfig` now includes standalone `ContentReplacementRule`s and `ProtoSchema`s; new `POST /api/admin/import/config` imports the parts of the bundle `/api/manipulation/import` doesn't already own (scheduled scans, fuzzer jobs, OpenRTB policies, standalone content rules, proto schemas), regenerating IDs on every entity
- `ScheduledScan` last-run outcome (#32): `LastRunStatus`/`LastRunError` columns (migration `AddScheduledScanLastRunStatus`); `ScheduledScanService` now polls the started scan job to a terminal status before recording the outcome — this also fixes a pre-existing bug where drift detection read an unfinished scan's (empty) findings
- `ProtoParser.FromJson`/`ToJson` (#34): replaced the hand-rolled comma/colon splitter with `System.Text.Json`, fixing incorrect parsing of field names containing `,` or `:`
- Real storage size stats (#35): `AdminController.GetStats` reports a real `database.estimatedSizeBytes` (SQLite `PRAGMA page_count * page_size`, Postgres `pg_database_size`) instead of the previous `count * 2048` / `count * 512` magic numbers

### feature/plugin-signing-audit-tiered-retention (previous)
- Plugin signing (#16): `PluginSignatureVerifier` validates `.manifest.json` (SHA-256 hash + RSA/ECDSA signature + X.509 cert) for each DLL; `PluginTrustStatus` enum; `Plugins:RequireSignedPlugins` + `Plugins:TrustedSignerThumbprints` config; Admin Plugins tab shows Trust/Signer columns; ADR at `docs/adr/0001-plugin-signing.md`; 7 new `PluginLoaderServiceTests`
- Audit tiered retention (#46): `AuditArchiveEntry` + `AuditArchive` table (migration `AddAuditArchive`); `ArchiveOlderThanAsync`/`PurgeArchiveOlderThanAsync` on `IAuditRepository`; `DataRetentionService` archives before purge; `POST/DELETE /api/admin/audit/archive`; DatabaseTab Audit Log card with archive/purge-archive sliders; ADR at `docs/adr/0002-audit-tiered-retention.md`

### feature/replay-fuzzer-tls-opt-in (previous)
- Replay/Fuzzer TLS validation is now opt-in, not always-on (finding #13)
- `BypassTlsValidation: bool` added to `StartReplayDto`, `StartFuzzerDto`, `ReplaySession`, `FuzzerJob`
- Two new named HTTP clients each: `IoTSpyReplay`/`IoTSpyReplayBypassTls`, `IoTSpyFuzzer`/`IoTSpyFuzzerBypassTls`
- Controller writes audit entry + both services emit `LogWarning` when bypass is active
- UI: "Bypass TLS validation" checkbox with orange warning badge in `ReplayPanel` and `FuzzerPanel`
- EF migration `AddBypassTlsValidation`; 4 new controller tests

### feature/scan-scope-consent-gate (previous)
- Scan scope enforcement: `ScanScope` model + `IScanScopeRepository` + `ScanScopeRepository`; `CidrHelper` (IPv4/IPv6 CIDR containment, bare-IP = /32 or /128); `ScanScopeController` at `GET/POST /api/scopes`, `PATCH /api/scopes/{id}/toggle`, `DELETE /api/scopes/{id}` (Admin-only writes); `AddScanScopes` EF Core migration; gate in `ScannerController.StartScan`: 403 if any active scopes exist and device IP is not in one
- Consent gate: `StartScanDto.ConsentAcknowledged: bool`; returns 400 if false (checked first, before device lookup); frontend consent checkbox in `ScannerPanel`; Start Scan button gated on both device selection and consent checkbox
- Admin UI: **Scan Scopes** tab added to the Admin page (add, enable/disable toggle, delete); `api/scanScopes.ts` + `hooks/useScanScopes.ts`
- All xUnit1051 `CancellationToken.None` → `TestContext.Current.CancellationToken` across 14 test files; pattern documented in `AGENT.md`
- 37 new backend tests (`CidrHelperTests` 17, `ScanScopeRepositoryTests` 7, `ScanScopeControllerTests` 9, scanner gate tests 4); 8 new frontend tests (`ScanScopesTab.test.tsx` 7, updated `ScannerPanel.test.tsx` +1)

### Gaps Batch 6 (previous)
- gRPC `.proto` upload: `ProtoParser` (regex-based field extraction), `ProtoSchema` model/repo, `ProtoSchemasController` at `/api/grpc/schemas`; `GrpcDecoder` accepts optional field map, populates `ProtobufField.FieldName`; `GrpcFrameType` enum + trailer frame detection (flag 0x80); 2 new EF migrations; 15 new backend tests; audit write-once trigger (`BEFORE UPDATE ON AuditEntries`); missing `scanner.css` created
- gRPC schema UI: `GrpcSchemasPanel` (upload form + schema list) wired as 8th tab in `ManipulationPanel`; `useGrpcSchemas` hook + `api/grpcSchemas.ts` client
- Orphaned panels wired: `ScannerPanel` and `OpenRtbPanel` added as 'scanner'/'openrtb' view modes in `DashboardPage`; `ScheduledScansPanel` added as tab within `ScannerPanel`
- Plugin UI: `PluginsTab` in Admin page (list + Admin-only reload); `api/plugins.ts` + `usePlugins` hook
- Protocol Proxy UI: `ProtocolProxyPanel` as 'Protocol Proxies' dashboard tab; MQTT + CoAP start/stop with config forms and live status; `api/protocolProxy.ts` + `useProtocolProxy` hook
- Dead code removed: `ReplacementRulesEditor.tsx` (superseded by `ContentRulesPanel`); spec-scoped rule CRUD removed from `useApiSpec`, `apispec.ts`, and `types/api.ts`
- 25 new frontend tests (4 new spec files + tab tests); total: 36 → 61 across 11 spec files

### Gaps Batch 5 (previous)
- CoAP: `CoapMessage` exposes `Block1/Block2` (`CoapBlockOption`), `ObserveValue`, `Size1/2`, `IsWellKnownCore` computed from already-decoded options
- DNS: EDNS0 OPT record (type 41) parsed from Additional section → `DnsMessage.EdnsRecord` with `UdpPayloadSize`, `DoBit`, option list
- WebSocket: `WebSocketDecodedFrame.DetectedSubProtocol` set to `Stomp`, `Wamp`, or `MqttOverWs` via payload heuristics
- MQTT: `MqttSessionAnalyzer` singleton accumulates per-topic stats + QoS-2 flow tracking across decoded packets
- Performance: `IManipulationRuleCache` / `ManipulationRuleCache` (30-s IMemoryCache, invalidated on all rule CRUD ops) cuts DB round-trips in the proxy hot path
- Frontend: `PanelPacketCapture.tsx` fully migrated from inline `style={{}}` to `panel-packet-capture.css` classes; hardcoded hex colors replaced with CSS variables
- 30 new backend tests (total: 745)

### Gaps Batch 4 (latest — now superseded by Batch 5)
- `GET /api/captures` now accepts `?headerQ=` for full-text search across `RequestHeaders` and `ResponseHeaders`
- Ring buffer capacity configurable via `PacketCapture:RingBufferCapacity` in `appsettings.json` (default 10 000); passed to `LockFreePacketRingBuffer` at startup
- Frontend component tests: `ManipulationPanel` (8), `PanelPacketCapture` (10), `SessionsPanel` (5) — total 13 → 36
- Playwright E2E: `manipulation.spec.ts` added; existing auth/captures/dashboard specs retained
- CSS token fix: `--color-error` alias added to both themes in `variables.css` (was undefined, causing invisible error text on admin pages)
- Export error feedback: `CaptureList` now surfaces a banner on download failure instead of silently swallowing the error
- `ResponseTab` save-as-asset: error state auto-resets after 4 s so the button re-enables for retry

### API & Backend Polish
- All list endpoints return `{ items, total, page, pageSize, pages }` pagination envelope
- Bulk rule enable/disable (`PATCH /api/manipulation/rules/bulk`), cancel-all scans (`POST /api/scanner/jobs/cancel-all`), bulk capture delete by filter
- Fuzzer export (`GET /api/manipulation/fuzzer/jobs/{id}/export`), scan export (`GET /api/scanner/jobs/{id}/export`), ruleset bundle export (`GET /api/manipulation/export`)
- `AuditEntry` extended with `OldValue`/`NewValue` JSON snapshots; all rule/spec/breakpoint mutations recorded with before/after diff
- Ruleset import (`POST /api/manipulation/import`) — always resets entity IDs to avoid conflicts

### Content Rules (post-Phase 22 decoupling)
`ContentReplacementRule` is now a first-class entity — no API spec required. Standalone rules are scoped by `Host` directly. The proxy pipeline (`ApiSpecMockService.ApplyMockAsync`) merges spec-attached and standalone rules by priority. UI: Manipulation panel has 8 tabs — **Traffic Rules** (header/body/status/delay/drop), **Breakpoints**, **Replay**, **Fuzzer**, **Content Rules** (all rules loaded immediately, host input is a live filter, no gate), **Assets** (promoted to top-level), **API Spec** (documentation-only: generate/import/export/refine), **gRPC Schemas** (added in Batch 6: upload `.proto` files for field-name decoding).

### Operational notes

**Linux packet capture (SharpPcap):** grant `CAP_NET_RAW`/`CAP_NET_ADMIN` to the *real* dotnet binary — `setcap` rejects symlinks:
```bash
sudo setcap cap_net_raw,cap_net_admin+eip "$(readlink -f $(which dotnet))"
```
Restart the API after running `setcap`. See AGENT.md for full details.

**JSON enum serialization:** `Program.cs` must configure `JsonStringEnumConverter` on *both* `AddControllers().AddJsonOptions(...)` and `AddSignalR().AddJsonProtocol(...)`. Missing the SignalR call causes numeric enum values in live-streamed captures, which crashes the frontend timeline.

See `docs/PHASES-COMPLETED.md` for full phase details and `docs/PHASES-ROADMAP.md` for the future roadmap.
See `docs/ARCHITECTURE.md` for the full architecture spec.
