# IoTSpy — Known Gaps & Technical Debt

This document tracks remaining gaps, known limitations, and technical debt. Items are prioritized by severity and implementation effort. See [PHASES-ROADMAP.md](PHASES-ROADMAP.md) for larger planned features.

---

## Active Gaps

| Gap | Description | Severity | Status | Notes |
|---|---|---|---|---|
| No LDAP / SAML SSO | Enterprise single sign-on not implemented | Low | Open | Deprioritized in Phase 16.5; deliberately deferred as [CODE-REVIEW-FINDINGS.md](CODE-REVIEW-FINDINGS.md) #59 (SSO/OIDC) — see that doc's rationale before picking this up |
| No distributed / multi-node mode | Single-instance proxy per deployment; horizontal scaling requires Redis backplane | Low | Open | Deprioritized in Phase 16.8; see Design Assumptions |
| No Bluetooth/Zigbee/Z-Wave | IoT protocols beyond IP-based networking are not supported | Low | Open | See Phase 17 for future work |

---

## API Completeness Gaps

All previously open filtering gaps have been resolved — see Resolved Items below.

---

## Security Hardening

### Input Validation — Remaining Items

`FluentValidation.AspNetCore` is registered; validators exist for `CreateRuleDto`, `UpdateRuleDto`, `CreateBreakpointDto`, `StartScanDto` (port range, concurrency, timeout). All previously open items resolved — see Resolved Items below.

---

## Testing Gaps

| Component | Current Status | Gap |
|---|---|---|
| Frontend components | 11 spec files (~61 tests) | All major panels covered: Manipulation (8 tabs incl. gRPC Schemas), PacketCapture, Sessions, Scanner, OpenRtb, ContentRules, CaptureList |
| End-to-end frontend | Auth + Captures + Dashboard + Manipulation specs (Playwright) | Load testing, security fuzzing |
| Load testing | None | Proxy throughput/latency baseline |
| Security fuzzing | Limited | AFL/libFuzzer on MQTT, DNS, CoAP parsers |

---

## Protocol Decoder Depth

Decoders exist for all major protocols but vary in depth:

| Protocol | Current depth | Enhancement opportunity |
|---|---|---|
| DNS | Basic query/response, label decompression, **EDNS0 OPT record parsed**, **DoH/DoT detection** (`DohDetector`/`DotDetector`, see `docs/CODE-REVIEW-FINDINGS.md` #44) | DNSSEC validation chain |
| CoAP | RFC 7252 decode, **Block-wise transfer (Block1/Block2), Observe option, .well-known/core** | — |
| gRPC | **Schema-less LPM + gRPC-Web trailer detection; `.proto` upload resolves field names** (`ProtoParser`, `ProtoSchemasController`) | — |
| MQTT | Full 3.1.1/5.0 decode, **topic statistics + QoS-2 flow tracking via MqttSessionAnalyzer** | — |
| WebSocket | Frame decode, **STOMP/WAMP/MQTT-over-WS sub-protocol detection** | Message sequence reconstruction across fragmented frames |

---

## Performance Considerations

### Known Hotspots

1. **Rule evaluation** — Regex caching with `ConcurrentDictionary<(Pattern, RegexOptions), Regex>` and `RegexOptions.Compiled` is in place in `RulesEngine`; **rule list now cached in `ManipulationRuleCache` (30-second sliding TTL, invalidated on all rule CRUD ops)**.
2. **JSON schema inference** — Recursive object traversal on large payloads; cache schema per `(host, path, method)` tuple
3. **Packet capture ring buffer** — Configurable via `PacketCapture:RingBufferCapacity` in `appsettings.json` (default 10 000); large capture bursts may still drop packets if capacity is not tuned.

### Optimization Opportunities

- Request deduplication in proxy pipeline (same URL + headers within N ms → single capture)
- Bloom filter pre-screen before full rule evaluation
- JSON schema inference caching per `(host, path, method)` tuple

---

## Design Assumptions

These assumptions should be revisited if requirements change:

1. **Single-instance proxy per deployment** — Assumes one API process and one pair of proxy servers (explicit + transparent). Horizontal scaling requires Redis backplane (Phase 16.8).
2. **SQLite for development, Postgres for production** — Current schema and migrations support both, but testing is primary on SQLite.
3. **Browser-based dashboard** — Assumes web client; no native mobile apps or CLI tools planned.
4. **JWT + API key auth only** — No SAML/LDAP support (Phase 16.5 deprioritized; see Active Gaps).
5. ~~**In-memory anomaly detector** — Resets on restart; no persistent baseline learning.~~ **Resolved.** `AnomalyDetector`'s per-host Welford baseline (duration/size means+variance, status-code histogram) is now checkpointed to the `HostBaselines` table every 30s and on graceful shutdown (`HostBaselineCheckpointService`), and restored at startup via `IAnomalyDetector.Seed(...)` — a restored host does not need to re-earn its warm-up sample count. This is host-keyed, not device-keyed, and does not add pattern-drift detection over the persisted history — see `PHASES-ROADMAP.md`'s narrowed "Behavioral fingerprinting" line for what's still open.
6. **Request-scoped repositories** — Each HTTP request gets a fresh EF Core DbContext; not suitable for long-running background tasks without scope management.

---

## Frontend Polish

All previously open items resolved — see Resolved Items below.

---

## Documentation Gaps

| Area | Status | Notes |
|---|---|---|
| Operator runbook | Basic | Need: troubleshooting guide, log interpretation, health check procedures |
| Deployment guides | Minimal | Docker Compose (dev) + Helm chart exist; need: bare-metal, systemd, production hardening guide |
| Performance tuning | None | Connection pool sizing, rate limit tuning, ring buffer sizing, cache strategies |
| Extension guides | Partial | Protocol decoders, AI providers covered; custom UI components and plugin development not covered |

---

## Security Audit Notes

- Root CA private key stored in local database (not HSM-protected); acceptable for research tool, not production
- API endpoints require Bearer token or API key; rate limiting enabled by default; dev overrides via `appsettings.Development.json`
- TLS MITM disabled in passthrough mode (`CaptureTls=false`); metadata extraction only
- SSL stripping requires explicit `SslStrip=true` flag; off by default
- **Audit log write-once enforced at DB level** — SQLite `BEFORE UPDATE` trigger on `AuditEntries` (`AuditWriteOnceTrigger` migration); deletions still permitted for data retention
- Breakpoint scripts execute arbitrary C# (Roslyn) and JavaScript (Jint) — no sandbox restrictions; operator-role required but worth documenting explicitly

---

## Suggestions for Next Contributors

Frontend component test coverage (manipulation, capture, sessions panels, and more) shipped in Gaps Batch 4 and after — see "Resolved Items" below. [CODE-REVIEW-FINDINGS.md](CODE-REVIEW-FINDINGS.md) is fully closed as of 2026-09-17 — its three deliberately-deferred items (#48 per-user data isolation, #56 project/workspace concept, #59 SSO/OIDC) are the main open questions carried forward from that review; this file's own tables above are the place to check for anything else.

See [AGENT-NOTES.md](AGENT-NOTES.md) for session setup and testing instructions.

---

## Resolved Items

### Dashboard layout persistence test-backfill (2026-09-17)
- ~~Dashboard layout persistence — zero frontend exposure~~ — Stale entry: the frontend was already fully wired (`frontend/src/api/dashboardLayouts.ts` full CRUD client, `frontend/src/hooks/useDashboardLayouts.ts` TanStack Query hook, `frontend/src/components/dashboard/DashboardLayoutsMenu.tsx` list/apply/save/set-default/delete UI, wired into `DashboardPage`, with a passing `DashboardLayoutsMenu.test.tsx`). What was actually missing was backend test coverage: added `DashboardControllerTests.cs` (ownership scoping on list, `UserId` set from the authenticated user on create, 404/403 on update and delete for missing/foreign layouts, partial-field update semantics) and `DashboardLayoutRepositoryTests.cs` (`GetByUserAsync` per-user isolation, `UpdateAsync` bumps `UpdatedAt`, CRUD round-trip). Also fixed a duplicate `DashboardLayout` interface in `frontend/src/types/api.ts` (one had `createdAt`/`updatedAt`, one didn't) and removed the unused `DashboardLayoutState` type.

### Gaps Batch 6 (2026-05-09)
- ~~gRPC `.proto` file upload / field name resolution~~ — `ProtoParser` (regex-based, no external dep) extracts per-message and flat field maps; `ProtoSchema` model + `IProtoSchemaRepository`; `ProtoSchemasController` at `GET/POST/DELETE /api/grpc/schemas`; `GrpcDecoder.DecodeAsync` overload accepts `IReadOnlyDictionary<int, string>` and populates `ProtobufField.FieldName`; EF migration `AddProtoSchemas`; 14 new tests (7 `GrpcDecoderTests` + 7 `ProtoParserTests`)
- ~~gRPC-Web trailer frame detection~~ — `GrpcDecoder.CanDecode` now accepts flag bytes `0x00`, `0x01`, and `0x80`; `GrpcFrameType` enum added (`Data`, `Trailer`); `GrpcMessage.FrameType` and `IsTrailerFrame` computed property set accordingly; trailer frames bypass protobuf field parsing; 4 new tests
- ~~Audit log not write-once at DB level~~ — SQLite `BEFORE UPDATE` trigger `prevent_audit_update` added via `AuditWriteOnceTrigger` EF migration; `RAISE(ABORT, ...)` blocks any row modification; deletions remain permitted for data retention
- ~~Frontend component tests insufficient~~ — 4 new spec files: `ScannerPanel.test.tsx` (9 tests), `OpenRtbPanel.test.tsx` (5 tests), `ContentRulesPanel.test.tsx` (6 tests), `CaptureList.test.tsx` (4 tests); total frontend tests: 36 → 61 across 11 spec files; also created missing `scanner.css`
- ~~gRPC proto schema UI not wired~~ — `GrpcSchemasPanel` component created with upload form + schema list (expand/collapse, delete); wired as 8th tab in `ManipulationPanel`; `useGrpcSchemas` hook + `api/grpcSchemas.ts` client; `ManipulationPanel.test.tsx` updated
- ~~ScannerPanel / OpenRtbPanel orphaned~~ — Both panels were fully built but never mounted; wired into `DashboardPage` as 'scanner' and 'openrtb' view modes in the view-mode toggle bar; `ScheduledScansPanel` wired as 'Scheduled Scans' tab within `ScannerPanel`
- ~~Plugin system UI~~ — `PluginsTab` added as 6th tab in Admin page; reads `GET /api/plugins`, shows protocol/name/version/load-status/assembly-path table; Admin-only Reload button hits `POST /api/plugins/reload`; `api/plugins.ts` + `usePlugins` hook
- ~~Protocol Proxy UI~~ — `ProtocolProxyPanel` added as 'Protocol Proxies' view mode in dashboard; MQTT card (listenPort/address, upstreamHost/port, logPayloads, topic filters) and CoAP card (same minus topic filters) each with start/stop toggle and live connection/message counters; `api/protocolProxy.ts` + `useProtocolProxy` hook (5-second status polling)

### Gaps Batch 5 (2026-05-08)
- ~~CoAP Block-wise transfer~~ — `CoapMessage` now exposes `Block1`, `Block2` (`CoapBlockOption` with `Num`, `More`, `Szx`, `BlockSize`), `Size1`, `Size2`, `ObserveValue`, and `IsWellKnownCore` as computed properties derived from the already-decoded options list; 7 new tests
- ~~CoAP Observe option~~ — `ObserveValue: uint?` populated from option 6; register (0) and sequence-number notifications handled
- ~~DNS EDNS0 OPT record~~ — `DnsDecoder` now parses OPT pseudo-RR (type 41) from the Additional section; `DnsMessage.EdnsRecord` carries `UdpPayloadSize`, `ExtendedRcode`, `Version`, `DoBit`, and `Options` list; `DnsRecordType.OPT = 41` added to enum; 3 new tests
- ~~WebSocket sub-protocol detection~~ — `WebSocketDecoder` inspects payload after unmasking; `WebSocketDecodedFrame.DetectedSubProtocol: WsSubProtocol?` is set for STOMP (command-line heuristic), WAMP (JSON array type-code), and MQTT-over-WS (MQTT fixed-header sniff); 9 new tests
- ~~MQTT topic statistics + QoS flow tracking~~ — `MqttSessionAnalyzer` singleton service accumulates per-topic stats (`MessageCount`, `TotalBytes`, `RetainedCount`, `LastSeen`, `QosDistribution`) and tracks QoS-2 handshake phases (Published → Received → Released → Completed) via `GetTopicStatistics()` / `GetQosFlows()`; registered in DI; 11 new tests
- ~~Rule evaluation re-queries DB on every request~~ — `IManipulationRuleCache` + `ManipulationRuleCache` (30-second sliding-expiry `IMemoryCache`) inserted between `ManipulationService` and the repository; all 6 rule-mutation paths in `ManipulationController` (Create, Update, Delete, BulkDelete, BulkUpdate, Import) call `ruleCache.Invalidate()`; 4 new tests
- ~~`PanelPacketCapture.tsx` inline styles~~ — All `style={{}}` inline objects replaced with semantic CSS classes in `panel-packet-capture.css`; hardcoded hex colors (`#fee`, `#c00`, `#d32f2f`, `#ccc`) replaced with `var(--color-error)`, `var(--color-danger)`, `var(--color-border)`; `color-mix()` used for error background tint

### Gaps Batch 4 (2026-05-04)
- ~~Full-text search in request/response headers~~ — `?headerQ=` query param added to `GET /api/captures`; `CaptureFilter.HeaderSearch` field added; `CaptureRepository.ApplyFilter` searches `RequestHeaders` and `ResponseHeaders` columns; 3 new repository tests
- ~~Rule regex caching~~ — Already implemented: `RulesEngine` has `ConcurrentDictionary<(Pattern, RegexOptions), Regex>` with `RegexOptions.Compiled`; closed as already resolved
- ~~Ring buffer size hardcoded~~ — `PacketCapture:RingBufferCapacity` setting added to `appsettings.json` (default 10 000); `ScannerExtensions.AddIoTSpyScanner(IConfiguration?)` reads the value and passes it to `LockFreePacketRingBuffer`
- ~~Manipulation/Capture/Sessions panels untested~~ — `ManipulationPanel.test.tsx` (8 tests: all 7 tabs + active CSS class), `PanelPacketCapture.test.tsx` (10 tests: tabs, device selector, start/stop, error display), `SessionsPanel.test.tsx` (5 tests: list, create form, validation); frontend test count: 13 → 36
- ~~No Playwright E2E suite~~ — Suite existed in `frontend/tests/` with auth.spec.ts (7 tests), captures.spec.ts, dashboard.spec.ts; added `manipulation.spec.ts` with 3 smoke tests covering tab visibility, rule list rendering, and tab switching

### Address Remaining Gaps (2026-05-04)
- ~~`PacketCaptureController` no tests~~ — `PacketCaptureControllerTests.cs` added with 29 tests covering devices CRUD, start/stop capture, packet filter API, freeze-frame (POST+GET), delete, protocol distribution, communication patterns, suspicious activity, PCAP import (happy path, bad extension, failure), PCAP export (filtered/unfiltered/no-data), and analyzer freeze/unfreeze/status; total backend tests now 712
- ~~IP/hostname field validation~~ — Gap is obsolete: `StartScanDto` was redesigned to accept `Guid DeviceId` instead of a free-text target; the controller derives the IP from the stored device record, so no raw string needs format-validating
- ~~File upload MIME type magic-byte check~~ — Already resolved in Gaps Batch 3 (see below); stale reference removed from active Security Hardening section

### Gaps Batch 3 (2026-05-02)
- ~~Content replacement: binary & SSE~~ — `FileStreamBodySource`, `RangeSlicedBodySource`, `SseStreamBodySource` added in Phase 22; proxy writer uses `IResponseBodySource` to bypass UTF-8 string path; HTTP range slicing for video scrubbing; 15 dedicated tests
- ~~Scanner job filtering not exposed~~ — `GET /api/scanner/jobs?status=&deviceId=&createdAfter=` added; `IScanJobRepository.GetAllAsync` extended with filter params
- ~~Fuzzer job filtering not exposed~~ — `GET /api/manipulation/fuzzer/jobs?status=&captureId=` added; `IFuzzerJobRepository.GetAllAsync` extended with filter params
- ~~File upload MIME magic-byte check~~ — `POST /api/apispec/assets` now reads first 12 bytes; rejects uploads whose magic signature doesn't match the declared/inferred MIME type; returns 415 with a descriptive error

### Gaps Batch 2 (2026-05-02)
- ~~`DELETE /api/manipulation/rules` (bulk)~~ — `DELETE /api/manipulation/rules/bulk` added; body `{ids:[...], all:false}`; `IManipulationRuleRepository.DeleteManyAsync` uses `ExecuteDeleteAsync`
- ~~`DELETE /api/scanner/jobs` (bulk)~~ — `DELETE /api/scanner/jobs/bulk` added; body `{status?, completedBefore?}`; `IScanJobRepository.DeleteByFilterAsync` uses `ExecuteDeleteAsync`
- ~~Session filtering not exposed~~ — `GET /api/sessions?createdByMe=true` filters by `CurrentUserId` via updated `IInvestigationSessionRepository.GetAllAsync(bool, Guid?, CancellationToken)`
- ~~No input validation framework~~ — `FluentValidation.AspNetCore` 11.3.0 added; validators for `CreateRuleDto`, `UpdateRuleDto`, `CreateBreakpointDto`, `StartScanDto` with regex pre-compile check and port range validation
- ~~`ManipulationController` no tests~~ — `ManipulationControllerTests.cs` added with 21 tests covering rules CRUD, bulk ops, breakpoints, fuzzer error paths, AI mock
- ~~Pagination on rules/breakpoints endpoints~~ — Both `GET /api/manipulation/rules` and `GET /api/manipulation/breakpoints` return `{ items, total, page, pageSize, pages }` with `?page=&pageSize=` params
- ~~Session filtering not exposed~~ — Resolved; see above
- ~~Missing keyboard shortcuts~~ — `useKeyboardShortcuts` hook (Escape/Delete/Ctrl+S); inline Escape + Ctrl+S in `RulesEditor` and `BreakpointsEditor`
- ~~Frontend hooks on manual useState/useEffect~~ — React Query (`@tanstack/react-query` v5) adopted for admin UI and audit logging

### Security Headers & Rate Limiting (2026-05-02)
- ~~Missing HTTP security headers~~ — Middleware added in `Program.cs`: `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `X-Permitted-Cross-Domain-Policies`, `Content-Security-Policy`, and `Strict-Transport-Security` (production-only)
- ~~Rate limiting off by default~~ — `RateLimit.Enabled=true` in `appsettings.json`; `RateLimit.Enabled=false` in `appsettings.Development.json` disables it in dev
- ~~Missing `appsettings.Production.json` example~~ — Template added at `src/IoTSpy.Api/appsettings.Production.json` with Postgres, HTTPS, rate limiting, data retention, and Serilog file sink

### CA Certificate Customization (2026-05-02)
- ~~CA certificate CN/O/C/validity hardcoded~~ — Added `CaCommonName`, `CaOrganization`, `CaCountry`, `CaValidityYears` to `ProxySettings`; `CertificateAuthority.GenerateRootCa()` reads these values; EF migration `AddCaCustomizationFields` applies defaults; `SettingsModal.tsx` exposes a new "CA Certificate" section; `PUT /api/proxy/settings` accepts the new fields; `ICertificateAuthority.RegenerateRootCaAsync()` atomically clears caches and regenerates using current settings

### API & Backend Polish (prior session)
- ~~Bulk operations missing~~ — Bulk rule enable/disable (`PATCH /api/manipulation/rules/bulk`), cancel-all scans (`POST /api/scanner/jobs/cancel-all`), bulk capture delete by filter
- ~~Missing export endpoints~~ — Fuzzer export (`GET /api/manipulation/fuzzer/jobs/{id}/export`), scan export (`GET /api/scanner/jobs/{id}/export`), ruleset bundle export (`GET /api/manipulation/export`), ruleset import (`POST /api/manipulation/import`)
- ~~No audit trail for config changes~~ — `AuditEntry` extended with `OldValue`/`NewValue` JSON snapshots; all rule/spec/breakpoint mutations recorded with before/after diffs
- ~~ScannerController no tests~~ — `ScannerControllerTests.cs` added with 7 tests
- ~~SessionsController no tests~~ — `SessionsControllerTests.cs` added with 14 tests

### Frontend Usability (prior session)
- ~~Missing confirmation dialogs~~ — `RulesEditor`, `BreakpointsEditor`, `FuzzerPanel`, `ScanJobList` (delete), and `SessionsPanel` all use `ConfirmDialog`
- ~~Export buttons not wired~~ — `CaptureList` toolbar has CSV/JSON/HAR export dropdown
- ~~Empty states need guidance~~ — All major panels have onboarding hints (manipulation rules, fuzzer, scanner, sessions)
- ~~Frontend capture list performance~~ — `react-window` + `AutoSizer` for virtual scrolling; infinite scroll via `loadMore`

### Phase 20 (Admin UI & Body Viewer)
- ~~Stray draft components in `src/IoTSpy.React/`~~ — `PacketAnalysisView.tsx` and `NetworkDeviceSelector.tsx` deleted; unique functionality migrated into `PacketListFilterable.tsx`

### Phase 11 (Multi-user & UX)
- ~~No Core model tests~~ — `IoTSpy.Core.Tests` project added with 30+ model default/enum tests
- ~~No multi-user support~~ — Multi-user RBAC with `User` model, `UserRole` enum (Admin/Operator/Viewer), user management endpoints
- ~~Dashboard not responsive~~ — Responsive CSS with mobile breakpoints (480px, 768px, 1024px)
- ~~TLS passthrough/SSL strip untested~~ — `TlsClientHelloParserTests` (13 tests), `TlsServerHelloParserTests` (11 tests), `SslStripServiceTests` (14 tests)

### Phase 16 (Deployment & Operations)
- ~~No HTTPS for the API itself~~ — `HttpsCertificateHolder` + `CertesLetsEncryptService` added in Phase 16.1; HTTPS on port 5001 with cert file or Let's Encrypt via `Certes`