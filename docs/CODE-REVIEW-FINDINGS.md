# Code Review Findings — Status & Remaining Work

Original review: 2026-05-09. This file is the **live status board** for the multi-angle review (backend correctness/security, frontend code & responsive UX, documentation accuracy, feature/use-case gaps). Items move from "Remaining" to "Completed" as PRs merge.

When picking up new work, consult the **Recommended next PRs** section at the bottom.

Severity legend: **Critical** = ship blocker · **High** = next-sprint · **Medium** = should-have · **Low** = nice-to-have.

---

## Status snapshot (2026-05-09 → present)

| State | Count | Items |
|---|---|---|
| ✅ Completed | 43 | #1, #2, #3, #4, #5, #6, #7, #8, #9, #10, #11, #12, #13, #14, #15, #16, #17, #18, #19, #20, #21, #22, #23, #24, #25, #26, #27, #28, #29, #30, #31, #32, #34, #35, #36, #39, #45, #46, #47, #49, #50, #55 |
| ⏳ In-flight (open PR) | 0 | — |
| 🟥 Critical remaining | 0 | — |
| 🟧 High remaining | 0 | — |
| 🟨 Medium remaining | 9 | #33, #37, #38, #40–#44, #48 |
| 🟩 Low remaining | 8 | #51–#54, #56–#59 |

PR history that closed items: **#59** (day-0 hotfixes), **#60** (doc accuracy), **#61** (test backfill), **#62** (responsive pass), **#63** (security hardening), **#66** (auth: session expiry redirect, multi-user login), **#68** (modal system: #20, #21, #49, #50), **#69** (crash resilience: #8), **feature/scan-scope-consent-gate** (scan scope enforcement + consent gate: #4, #45), **feature/replay-fuzzer-tls-opt-in** (Replay/Fuzzer TLS bypass opt-in: #13), **feature/plugin-signing-audit-tiered-retention** (plugin signing: #16; audit tiered retention: #46), **feature/config-export-import-polish** (incomplete-feature polish: #29, #30, #31, #32, #34, #35), **feature/capture-curl-diff** (Researcher persona: #36, #39, #55).

---

## 🟥 Critical — all resolved ✅

---

## 🟧 High — all resolved ✅

---

## 🟨 Medium — remaining

### Incomplete shipped features

**33. Report covers scan findings only** — `ReportService.cs:50` only loads `ScanJob` + `ScanFinding`. No captures, TLS metadata, annotations, MQTT/DNS messages. Not a usable pen-test deliverable. Redesign report sections + template system.

### Missing user stories (high-value)

**37. HAR import** — export exists but no `POST /api/captures/import/har`. Developers can't seed IoTSpy from a browser-captured HAR.

**38. Replay against override base URL** — `StartReplayDto.Host` exists but UX/coverage unclear. Verify end-to-end and surface in the UI.

**40. In-app SignalR notification when a rule/breakpoint fires** — `AlertingService` does external webhooks/email/Slack only. Add an in-app channel via the existing collaboration hub.

**41. User mgmt UI may be incomplete** — `UsersTab.tsx` exists but create/edit wiring to `POST /api/auth/users` not verified. Audit the file before assuming work is needed.

**42. DB backup/restore endpoints absent** — admin UI has purge only. Add `GET /api/admin/backup` (SQLite `.backup` / `pg_dump` invocation) + `POST /api/admin/restore`.

**43. `DataRetentionService` thresholds are config-file-only** — no runtime API. Add `GET / PUT /api/admin/retention` + UI in DatabaseTab.

### Protocol coverage gaps

**44. AMQP 1.0** (Azure IoT Hub, ActiveMQ — high IoT relevance), **RTSP/RTP** (IP cameras — frequent IoT finding), **MQTT-SN** (constrained-device UDP variant), **DoH/DoT detection** (devices increasingly evade DNS inspection). All on roadmap, none shipped. Slot: `IoTSpy.Protocols`. One PR per protocol.

### Trust & safety

**48. No per-user data isolation** — Viewer-role users see all captures/scans across the instance. For shared-instance deployments this leaks data. Add row-level ownership filter on capture/scan/device queries. Touches many controllers; bundle as a single multi-controller PR.

---

## 🟩 Low — remaining

**51. OpenTelemetry tracing absent** — Serilog only. For multi-container deployments, cross-service trace correlation is missing.

**52. Prometheus metric surface thin** — 6 metrics. Missing: breakpoint hits, fuzzer throughput, rule match rate, per-protocol capture volume, DB query latency, SignalR connection count.

**53. No bundled Grafana dashboards** despite Helm chart shipping.

**54. No on-call runbook in `docs/`** — what to do when the proxy stops intercepting, how to recover from corrupt SQLite, how to rotate JWT secret without logging everyone out.

**56. Project/workspace concept absent** — no aggregate over Device + Session + ScanJob to namespace per-engagement artifacts.

**57. CVSS override unavailable on findings** — `ScanFinding.CvssScore` is OSV-derived and read-only. Add `PATCH /api/scanner/findings/{id}` for tester override.

**58. Scheduled scans against a target list** — `ScheduledScan` is FK'd to a single `Device`. Support tag/CIDR-based target lists.

**59. SSO/OIDC** — JWT-only with local password store. Blocks enterprise adoption.

---

## ✅ Completed

Each entry includes the closing PR. Use `gh pr view <num>` for the full diff and test plan.

### Critical (4 of 6)

- **#1 — `GET /api/packet-capture/status` was a hardcoded stub** [PR #59] — now reads `IPacketCaptureService.IsCaptureActive`. Test split into `_ReflectsServiceIsCaptureActive` + `_ReturnsFalseWhenCaptureInactive`.
- **#2 — Admin role-case mismatch silently 403'd everyone** [PR #59] — `[Authorize(Roles = "Admin")]` → `"admin"` on `PluginsController.Reload` and `SessionsController.Delete`. Regression test in `PluginsControllerTests.Reload_RequiresAdminRole_LowercaseToMatchAuthServiceClaim`.
- **#3 — Path traversal via `ReplacementFilePath`** [PR #59] — new `AssetsPaths.ResolveReplacementFilePath` rejects path separators / `..` and pins resolved paths inside `AssetsDirectory`. Five-payload `[Theory]` regression guard in `ContentRulesControllerTests.Create_RejectsReplacementFilePathOutsideAssetsDirectory`.
- **#5 — Five CSS variables undefined globally** [PR #59] — `--color-text-secondary`, `--color-error-bg`, `--color-bg-alt`, `--color-accent`, `--color-input-bg` aliased in both theme blocks of `variables.css`.
- **#6 — `PanelPacketCapture` infinite-render risk** [PR #62] — destructured stable `useCallback` refs; `useEffect` no longer depends on the unstable `analysis` object literal.
- **#7 — `PanelPacketCapture` 3-column layout broken <768px** [PR #62] — `responsive.css` stacks `.ppc-root` vertically; inspector capped at 50 vh.

### High security & correctness (7 of 8)

- **#13 — Replay/Fuzzer silently bypassed TLS validation** [branch: feature/replay-fuzzer-tls-opt-in] — `BypassTlsValidation: bool = false` added to `StartReplayDto`, `StartFuzzerDto`, `ReplaySession`, and `FuzzerJob` models. `ManipulationExtensions` now registers `IoTSpyReplay`/`IoTSpyFuzzer` (TLS-validating, default) and `IoTSpyReplayBypassTls`/`IoTSpyFuzzerBypassTls` (opt-in bypass) named clients. `ReplayService` and `FuzzerService` select the client by flag; `ManipulationService` passes the flag through. Controller writes an `AuditEntry` (action `ReplayBypassTls`/`FuzzerBypassTls`) and both services log `LogWarning` when bypass is active. UI: checkbox in `ReplayPanel` and `FuzzerPanel` with an orange "Warning: insecure" badge. EF migration `AddBypassTlsValidation`. 4 new controller tests.

- **#9 — `ProtoSchemasController.Upload` had no size limit** [PR #63] — `[RequestSizeLimit(1 MB)]` + length guard on both upload paths.
- **#10 — `ProtoSchemasController.Delete` was not Admin-restricted** [PR #63] — `[Authorize(Roles = "admin")]` added; regression test asserts the attribute via reflection.
- **#11 — Admin purge endpoints OOM-risky** [PR #63] — `ToListAsync()` + `RemoveRange()` + `SaveChangesAsync()` replaced with `ExecuteDeleteAsync(ct)`.
- **#12 — `ManipulationRuleCache` TOCTOU race** [PR #63] — switched to `IMemoryCache.GetOrCreateAsync`.
- **#14 — Audit write-once trigger SQLite-only** [PR #63] — migration now branches on `migrationBuilder.ActiveProvider`; emits plpgsql trigger function for Npgsql, preserves existing SQLite trigger.
- **#15 — `ScannerController` accepted unbounded `PortRange`** [PR #63] — `PortScanner.MaxResolvedPorts = 10_000` cap; controller rejects port-range strings >256 chars.

### High frontend / UX (5 of 9)

- **#17 — Four shipped controllers had zero tests** [PR #61] — 49 new tests across `ContentRulesController`, `ProtoSchemasController`, `PluginsController`, `ProtocolProxyController`. Includes day-0 regression guards.
- **#18 — `PanelPacketCapture` swallowed export failures silently** [PR #62] — `setExportError` + `role="alert"` banner.
- **#19 — `PacketInspector` close button was `<button>x</button>`** [PR #62] — `aria-label="Close packet inspector"` and renders `×`.
- **#20 (partial) — Inline-styled Batch-6 components** [PR #62] — `PluginsTab` migrated to `admin.css` conventions + `plugins-tab.css`; `ProtocolProxyPanel` fully migrated to new `protocol-proxy.css`. Remainder (RulePreviewModal, ContentRulesPanel modal, AssetLibrary, PacketInspector body) is the modal-system PR (#20 remainder, above).
- **#22 — `ProtocolProxyPanel` un-overridable inline grid** [PR #62] — extracted to `.protocol-proxy-grid` / `.protocol-proxy-form-grid`; mobile breakpoint collapses to single column.
- **#23 — `ScannerPanel` body lacked base flex declarations** [PR #62] — base rules added to `scanner.css`; responsive override now has something to override.
- **#24 — Admin tabs (6) clipped on mobile** [PR #62] — `overflow-x: auto` + `flex-shrink: 0` in mobile breakpoint.
- **#25 — `PluginsTab` table missing `admin-table-wrap`** [PR #62] — table wrapped; assembly-path `<code>` no longer overflows page.
- **#20 (complete) — Inline-styled modals** [PR #68] — `RulePreviewModal`, `ContentRulesPanel` modal, `AssetLibrary`, `PacketInspector` fully migrated: component-scoped CSS files (`modal.css` extensions, `content-rules.css`, `asset-library.css`, `packet-inspector.css`), all `style={{}}` with hex literals replaced by `var(--color-*)` tokens.
- **#21 — Focus traps in modals** [PR #68] — new `useFocusTrap` hook (Tab/Shift+Tab cycles within container, auto-focuses first element on open); applied to `RulePreviewModal`, `AssetPickerModal` inside `ContentRulesPanel`. Escape handler uses stable `useRef` pattern.
- **#49 — Tab groups lack ARIA semantics** [PR #68] — `PacketInspector`, `ManipulationPanel`, `PanelPacketCapture` now have `role="tablist"` / `role="tab"` / `aria-selected` / `aria-controls` / `role="tabpanel"`. Tests updated to query `role="tab"`.
- **#50 — Capture list rows not keyboard-accessible** [PR #68] — `RulePreviewModal` capture list rows: `role="listbox"` container, each row has `role="option"` + `aria-selected` + `tabIndex={0}` + `onKeyDown` Enter/Space handler.

### High doc accuracy (3 of 3)

- **#26 — Test count contradictions across CLAUDE.md / AGENT.md / README.md / ARCHITECTURE.md** [PR #60, bumped in follow-up to PR #61, PR #66] — 771 backend / 94 frontend (as of modal-system PR). Re-bump after each test-adding PR.
- **#27 — Controller list said 19, claimed 20** [PR #60] — added `ProtoSchemas` to enumeration; locked at 20.
- **#28 — Manipulation panel "7 tabs" claim** [PR #60] — corrected to 8 (gRPC Schemas added in Batch 6).

### Critical (1 of 2)

- **#8 — No crash resilience for live captures** [PR #69] — `PacketCaptureCheckpointService` (singleton + hosted service) flushes the ring buffer to SQLite on a 1-second cadence; startup recovery reads the most-recent N packets back into the ring buffer before the HTTP listener opens. `IPacketRepository` extended with `AddRangeAsync`, `GetMaxCaptureIndexAsync`, `GetRecentAsync`, `DeleteAllAsync`. `ClearCapturesAsync` and `StartCaptureAsync` reset the flush watermark. N+1 delete bug in `ClearAllAsync` fixed with `ExecuteDeleteAsync`. 11 new tests.

### Critical (2 of 2) — all complete ✅

- **#4 — No scan scope enforcement** [branch: feature/scan-scope-consent-gate] — `ScanScope` model + `IScanScopeRepository` + `ScanScopeRepository`; `CidrHelper` (IPv4/IPv6 CIDR matching, bare IP as /32 or /128); `ScanScopeController` at `GET/POST/PATCH /{id}/toggle/DELETE /api/scopes` (Admin-only writes); `AddScanScopes` EF Core migration; gate wired into `ScannerController.StartScan`: if any active scopes exist and the device IP is not in one, returns 403. Admin UI: **Scan Scopes** tab in the Admin page (add/enable/disable/delete scopes). 17 new `CidrHelperTests` + 7 `ScanScopeRepositoryTests` + 9 `ScanScopeControllerTests` + 4 new gate tests in `ScannerControllerTests`. All xUnit1051 `CancellationToken.None` warnings across the entire test suite resolved in the same branch.
- **#45 — No "I am authorized" consent gate** [branch: feature/scan-scope-consent-gate] — `StartScanDto` gains `ConsentAcknowledged: bool`; `ScannerController.StartScan` returns 400 if it is false (checked before any device lookup). Frontend: consent checkbox added to `ScannerPanel`; **Start Scan** button stays disabled until both a device is selected and the checkbox is ticked. `StartScanRequest` TypeScript type updated; `ScannerPanel.test.tsx` updated with consent-gate assertions.

### Trust & safety (4 of 4) ✅

- **#16 — No code signing on plugin loader** [branch: feature/plugin-signing-audit-tiered-retention] — `PluginSignatureVerifier` validates a per-DLL `.manifest.json` (SHA-256 hash, RSA/ECDSA signature over hash bytes, X.509 signer certificate). `PluginLoaderService` calls the verifier for every DLL; if `Plugins:RequireSignedPlugins=true`, unsigned/untrusted DLLs are rejected before loading; otherwise they load with a warning. `Plugins:TrustedSignerThumbprints` allowlist in `appsettings.json`. `PluginTrustStatus` enum (`Trusted`, `Untrusted`, `ManifestMissing`, `ManifestInvalid`, `HashMismatch`, `SignatureInvalid`) propagated to `PluginInfo`, API response, and frontend. Admin Plugins tab shows Trust and Signer columns with colour-coded badges. ADR at `docs/adr/0001-plugin-signing.md`. 7 new tests in `PluginLoaderServiceTests`.

- **#46 — Audit log DELETE not blocked** [branch: feature/plugin-signing-audit-tiered-retention] — Tiered retention: `AuditArchiveEntry` model + `AuditArchive` table (EF migration `AddAuditArchive`). `IAuditRepository` gains `ArchiveOlderThanAsync` and `PurgeArchiveOlderThanAsync`. `DataRetentionService` archives first, then purges archive after a configurable `AuditArchivePurgeDays`. Manual controls: `POST /api/admin/audit/archive?olderThanDays=N` and `DELETE /api/admin/audit/archive?olderThanDays=N` (Admin-only). `GetStats` includes `archiveCount`/`oldestArchiveTimestamp`. `DatabaseTab` has Audit Log card with age-slider Archive and Purge-archive buttons. ADR at `docs/adr/0002-audit-tiered-retention.md`. `DataRetentionServiceTests` registers `IAuditRepository` in its DI container.

- **#47 — Scanner `MaxConcurrency` defaulted to 100** [PR #63] — default lowered to 25; user values clamped to ≤100.

### Incomplete-feature polish (6 of 6) ✅

- **#29 — HAR export emits empty `headers: []`** [branch: feature/config-export-import-polish] — new `IoTSpy.Core.Utilities.HttpHeaderParser` parses the raw `Name: Value\r\n...` text actually written to `CapturedRequest.RequestHeaders`/`ResponseHeaders` by the proxy pipeline (the "JSON-serialized" doc comment on those properties was wrong). `CapturesController.BuildHar` populates real header arrays for both request and response; `ParseContentType` (used by the streaming-asset export path) fixed the same way instead of its previous silently-failing `JsonDocument.Parse`. New `HttpHeaderParserTests` (6 tests) + HAR header-population regression test in `CaptureExportTests`.
- **#30 / #31 — Config export/import round-trip** [branch: feature/config-export-import-polish] — `AdminController.ExportConfig` now includes standalone `ContentReplacementRule`s (`ApiSpecDocumentId == null`) and `ProtoSchema`s. New `POST /api/admin/import/config` (`ImportConfigDto`) imports scheduled scans, fuzzer jobs, OpenRTB PII policies, standalone content rules, and proto schemas — the parts of the export bundle `/api/manipulation/import` doesn't already own — regenerating every entity's `Id` per the existing `ImportRuleset` pattern. Content rules without a `Host` are skipped and counted, not thrown. Audit entry written on import. 4 new `AdminControllerTests`.
- **#32 — `ScheduledScan` has no last-run outcome** [branch: feature/config-export-import-polish] — `LastRunStatus`/`LastRunError` columns (migration `AddScheduledScanLastRunStatus`); reused the existing `LastScanJobId` FK rather than adding a duplicate. `ScheduledScanService` now polls the started scan job to a terminal status (`Completed`/`Failed`/`Cancelled`, 2s interval, 5min cap) before recording the outcome and before running drift detection — incidentally fixing a pre-existing bug where drift detection read an unfinished scan's (empty) findings, since `StartScanAsync` returns immediately and runs the scan on a background `Task.Run`. Device-not-found and exception paths also record `Failed` + a reason. 4 new `ScheduledScanServiceTests`.
- **#34 — `ProtoParser.FromJson`/`ToJson` fragile hand-rolled parser** [branch: feature/config-export-import-polish] — both replaced with `System.Text.Json.JsonSerializer`; on-disk `{"1":"name"}` shape unchanged since `Dictionary<int,string>` serializes int keys as JSON string keys natively. Fixes incorrect parsing of field names containing `,` or `:`. 2 new `ProtoParserTests`.
- **#35 — `AdminController.GetStats` magic-number storage estimates** [branch: feature/config-export-import-polish] — replaced the fabricated `count * 2048` / `count * 512` per-entity numbers with one real `database.estimatedSizeBytes` (SQLite: `PRAGMA page_count * page_size`; Postgres: `pg_database_size(current_database())`), read via raw ADO (`SqlQueryRaw<T>` can't compose over non-composable `PRAGMA`/PRAGMA-like statements).

### Researcher persona (3 of 3) ✅

- **#36 — Capture-to-curl** [branch: feature/capture-curl-diff] — `GET /api/captures/{id}/curl` reconstructs a runnable curl command (`-X`, `-H` per header via the existing `HttpHeaderParser`, `--data-raw` for a non-empty body, default-port suppression, POSIX shell-quoting). Backend only — no UI button yet (tracked separately if wanted).
- **#39 — Body full-text search on captures: corrected, not missing** [branch: feature/capture-curl-diff] — investigation found `?q=` already searches `RequestBody`/`ResponseBody` via `CaptureFilter.BodySearch` (`CaptureRepository.ApplyFilter`); the original item's claim that `?q=` is host/URL-only and body search doesn't exist was stale. Real SQLite FTS5 was evaluated and intentionally **not** built: zero existing FTS5 infrastructure in the repo, and adding it would mean SQLite-only sync triggers plus a separate Postgres tsvector/pg_trgm path — cross-provider complexity ruled out to avoid breaking Postgres parity. Landed instead: a `MinSearchTermLength` (3-char) guard on `?q=`/`?headerQ=` in `CapturesController.List`, since a 1-2 character `LIKE '%x%'` is a near-full-table scan regardless of provider (a leading wildcard defeats a B-tree index on both SQLite and Postgres). New repository test covers `BodySearch` (previously untested despite already working).
- **#55 — Capture diff endpoint** [branch: feature/capture-curl-diff] — `GET /api/captures/diff?a=&b=` returns a structural diff (method/URL/status-changed flags, per-header add/remove/change entries via `HttpHeaderParser`, request/response body-equality flags) with no external diff library — a future UI renders it however it likes. 400 on `a == b`, 404 naming whichever capture(s) are missing.

---

## Recommended next PRs

Updated after feature/capture-curl-diff landed. All Critical, High, and the "incomplete-feature polish" + "Researcher persona" Medium/Low items are now resolved. Remaining PRs in priority order:

1. **User-story PRs (medium, high-value)** — pick one per persona-PR:
   - Pen-tester: #38 (replay base-URL override end-to-end), #57 (CVSS override `PATCH /api/scanner/findings/{id}`), #56 (project/workspace concept)
   - Developer: #37 (`POST /api/captures/import/har`)
   - Admin: #41 (audit `UsersTab.tsx` create/edit wiring), #42 (backup/restore endpoints), #43 (`GET/PUT /api/admin/retention` + DatabaseTab UI), #40 (in-app SignalR alerts via collaboration hub)

2. **Report redesign** — #33. `ReportService` only covers scan findings today; not a usable pen-test deliverable without captures, TLS metadata, annotations, and protocol messages.

3. **Per-user data isolation PR** — #48. Row-level ownership filter helper in `IoTSpy.Storage`; applied across captures, scans, and device queries. Touches many controllers; bundle as a single multi-controller PR.

4. **Protocol coverage** — #44. One PR per protocol; AMQP 1.0 and RTSP/RTP first (highest IoT relevance). Slot: `IoTSpy.Protocols`.

5. **Operational observability** — #51 (OTEL tracing), #52 (broader Prometheus metrics), #53 (Grafana dashboards), #54 (runbook). Each independent; pick up in any order.

6. **SSO/OIDC** — #59. Largest single feature in the Low tier; only prioritize if a customer is asking.

Each item above is sized to fit a focused PR. Avoid bundling across categories — the cleaner the diff, the easier the review.

## Verification commands

After any test-adding PR, re-bump the `[Fact]/[Theory]` grep count in CLAUDE.md, AGENT.md, README.md, ARCHITECTURE.md:

```bash
grep -rE "^\s*\[(Fact|Theory)" --include="*.cs" src/IoTSpy.*.Tests src/IoTSpy.Api.IntegrationTests | wc -l
ls src/IoTSpy.Api/Controllers | wc -l
ls src/IoTSpy.Storage/Migrations/*.cs | grep -vE "(Designer|Snapshot)" | wc -l
grep -rE "\[Http" --include="*.cs" src/IoTSpy.Api/Controllers | wc -l
```
