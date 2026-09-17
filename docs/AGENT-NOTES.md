# IoTSpy — Claude Code Session Notes

This document is for Claude Code AI agents resuming work on the IoTSpy codebase. It covers quick setup, testing procedures, and session handoff information.

---

## Quick Setup

### Backend
```bash
cd /path/to/IoTSpy

# Set JWT secret via .NET user secrets (one-time; stored outside the repo)
dotnet user-secrets set "Auth:JwtSecret" "your-32-char-minimum-secret" \
  --project src/IoTSpy.Api

# Build & test
dotnet build
dotnet test

# Run the API (user secrets are loaded automatically in Development)
dotnet run --project src/IoTSpy.Api
# → http://localhost:5000
# → Scalar API docs: http://localhost:5000/scalar (Development mode only)
```

### Frontend
```bash
cd frontend
npm install
npm run dev
# → http://localhost:5173
```

### VS Code (optional)
`.vscode/launch.json` and `.vscode/tasks.json` are committed. Use the **"Full Stack: API + Frontend"** compound launch config (F5) to start both with the debugger attached. User secrets are loaded automatically — no env var export needed.

### Docker
```bash
docker compose up -d
# API: http://localhost:5000
# Proxy: port 8888
```

---

## Project Structure at a Glance

```
IoTSpy.Core           — Domain models, interfaces, enums (no infrastructure deps)
IoTSpy.Proxy          — TLS MITM, TLS passthrough/SSL stripping, WebSocket/MQTT/CoAP proxies
IoTSpy.Protocols      — MQTT, MQTT-SN, DNS, CoAP, WebSocket, gRPC, Modbus, OpenRTB, RTSP/RTP, AMQP 1.0, DoH detection, telemetry decoders
IoTSpy.Scanner        — Port scan, fingerprinting, CVE lookup, packet capture
IoTSpy.Manipulation   — Rules engine, scripts, replay, fuzzer, AI mock, API spec, content replacement
IoTSpy.Storage        — EF Core DbContext + repositories (SQLite/Postgres)
IoTSpy.Api            — ASP.NET Core host (22 controllers, 3 SignalR hubs)
IoTSpy.*.Tests        — Unit + integration tests (1183 executed)
frontend/             — Vite 6 + React 19 + TypeScript dashboard
docs/                 — ARCHITECTURE.md, PLAN-INDEX.md, PHASES-*.md, GAPS.md, etc.
```

---

## Key Abstractions & Patterns

### Multi-user RBAC
- `User` model with `UserRole` enum (Admin/Operator/Viewer)
- JWT claims include `NameIdentifier` (user ID) + `Role`
- Admin-only endpoints guarded by `[Authorize(Roles = "admin")]` (lowercase — a case-mismatch here silently 403'd everyone; see `docs/CODE-REVIEW-FINDINGS.md` #2)
- Backward-compatible with legacy single-user mode (falls back to `ProxySettings.PasswordHash`)

### Audit log
- `AuditEntry` model + `IAuditRepository`
- Tracks login, user CRUD, destructive operations
- Immutable by design (treated as event log)

### Repository pattern
- All repos are **scoped** (one per HTTP request) because they depend on scoped EF Core `DbContext`
- CRUD methods async; use with dependency injection

### SignalR hubs
- `TrafficHub` at `/hubs/traffic` — HTTP/HTTPS capture streaming (per-device groups)
- `PacketCaptureHub` at `/hubs/packets` — Packet capture streaming + PCAP import progress
- `CollaborationHub` at `/hubs/collaboration` — Real-time session collaboration (Phase 15)
- Token via `?access_token=` query parameter

### Resilience (Polly)
- Per-host circuit breaker: 50% failure rate over 30s → break for 60s
- Connect timeout: 15s; TLS handshake: 10s (configurable)
- Registered in `Program.cs` as named pipelines: `iotspy-connect`, `iotspy-tls`

### Rules engine
- Declarative match → action model
- Phases: `ManipulationPhase.Request` or `ManipulationPhase.Response`
- Actions: `ModifyHeader`, `ModifyBody`, `OverrideStatusCode`, `Drop`, `Delay`
- Execute in priority order; can chain rules

---

## Testing Before Commit

**CRITICAL:** All backend tests must pass before committing (1183 executed as of 2026-09-17; re-check via the commands in `CLAUDE.md` rather than trusting this number, it drifts fast).

```bash
# Full test suite
dotnet test

# Single project
dotnet test src/IoTSpy.Protocols.Tests/IoTSpy.Protocols.Tests.csproj

# Coverage report (if using Coverlet)
dotnet test /p:CollectCoverage=true /p:CoverageFormat=lcov

# Frontend tests
cd frontend && npm test
```

---

## Adding Features: Checklist

### Backend feature
1. **Define domain model** in `IoTSpy.Core/Models/` (no infrastructure deps)
2. **Create repository interface** in `IoTSpy.Core/Interfaces/`
3. **Implement repository** in `IoTSpy.Storage/Repositories/`
4. **Add DbSet** in `IoTSpyDbContext` if new entity
5. **Create EF Core migration** — `dotnet ef migrations add <MigrationName> --project src/IoTSpy.Storage --startup-project src/IoTSpy.Api`
6. **Add controller endpoints** in `IoTSpy.Api/Controllers/` or extend existing
7. **Register in DI** — `Program.cs`
8. **Write tests** — `IoTSpy.*.Tests` (unit + integration)
9. **Test locally** — `dotnet test` + manual via Scalar/frontend
10. **Document** — Update README.md, ARCHITECTURE.md, PHASES-*.md as needed

### Frontend feature
1. **Add API client** in `frontend/src/api/*.ts`
2. **Add TypeScript types** in `frontend/src/types/api.ts`
3. **Add custom hook** in `frontend/src/hooks/*.ts` (e.g., `useCaptures`, `useScanner`)
4. **Create component(s)** in `frontend/src/components/*/`
5. **Wire into page/panel** (e.g., `DashboardPage`, `ManipulationPanel`)
6. **Add CSS** in `frontend/src/styles/` or component-scoped
7. **Write component tests** in `frontend/src/__tests__/`
8. **Test manually** via dev server `npm run dev`
9. **Check responsive design** — 480px, 768px, 1024px breakpoints

---

## EF Core Migrations

### When to create a migration
- Adding/removing a table (DbSet)
- Adding/removing a column
- Changing column type, nullability, or max length
- Adding/removing indexes or constraints

### How to create
```bash
# From repo root
dotnet ef migrations add DescriptiveNameHere \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api
```

### How to apply
```bash
# Development (auto-applies on app startup via `context.Database.Migrate()`)
dotnet run --project src/IoTSpy.Api

# Or manually
dotnet ef database update \
  --project src/IoTSpy.Storage \
  --startup-project src/IoTSpy.Api
```

### SQLite quirks
- No `ALTER COLUMN` with NOT NULL defaults; use `.Sql()` workaround
- DateTimeOffset stored as Unix milliseconds (`long`) via `ValueConverter`
- Add stub `.Designer.cs` for migration discovery if compilation fails

---

## Linux Packet Capture Setup

Required before packet capture features work:

```bash
# Install libpcap
sudo apt-get install libpcap-dev

# Grant dotnet binary the required capabilities
# IMPORTANT: Use readlink -f to get the real binary, not the symlink
sudo setcap cap_net_raw,cap_net_admin+eip "$(readlink -f $(which dotnet))"

# Verify
getcap "$(readlink -f $(which dotnet))"  # should show cap_net_raw,cap_net_admin+eip
```

Restart the API after running `setcap`.

---

## Configuration Quick Reference

All values in `src/IoTSpy.Api/appsettings.json` can be overridden via environment variables (double-underscore notation):

| Setting | Env var | Default | Notes |
|---|---|---|---|
| `Auth:JwtSecret` | `Auth__JwtSecret` | (required) | ≥32 chars |
| `Database:Provider` | `Database__Provider` | `sqlite` | `sqlite` or `postgres` |
| `Database:ConnectionString` | `Database__ConnectionString` | `Data Source=iotspy.db` | — |
| `AiMock:Provider` | `AiMock__Provider` | `claude` | `claude`, `openai`, or `ollama` |
| `AiMock:Model` | `AiMock__Model` | `claude-sonnet-4-6` | Model ID |
| `AiMock:ApiKey` | `AiMock__ApiKey` | (empty) | API key for provider |
| `AiMock:BaseUrl` | `AiMock__BaseUrl` | (empty) | Ollama base URL |
| `Serilog:MinimumLevel` | `Serilog__MinimumLevel` | `Information` | `Debug`, `Information`, `Warning`, `Error` |
| `RateLimit:Enabled` | `RateLimit__Enabled` | `true` | Enable/disable rate limiting |
| `RateLimit:PermitLimit` | `RateLimit__PermitLimit` | `100` | Max requests per window |
| `RateLimit:WindowSeconds` | `RateLimit__WindowSeconds` | `60` | Time window in seconds |

---

## Common Debugging Scenarios

### "Timeline crash" — Enum serialization
**Symptom:** Frontend crashes when rendering capture list.  
**Cause:** Missing `JsonStringEnumConverter` on SignalR.  
**Fix:** Verify both calls in `Program.cs`:
```csharp
.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
```

### "Packet capture unavailable"
**Symptom:** Packet capture button disabled; warning in API logs.  
**Cause:** SharpPcap initialization failed (usually missing `CAP_NET_RAW` on Linux).  
**Fix:** Run `setcap` command above; restart API.

### "SSL certificate rejected on iOS"
**Symptom:** iOS device rejects the IoTSpy CA certificate.  
**Cause:** CA cert validity > 397 days, or AKI in wrong form, or IP SAN format incorrect.  
**Fix:** Delete all certs, let proxy regenerate: `sqlite3 iotspy.db "DELETE FROM Certificates WHERE IsRootCa = 0;"`

### "Test timeout — EF Core in-memory"
**Symptom:** `OperationCanceledException` in integration tests.  
**Cause:** In-memory SQLite too slow for large datasets.  
**Fix:** Reduce test data size, increase timeout, or parallelize tests with caution (in-memory isn't thread-safe by default).

---

## Hot Reload / Development Workflow

### Backend
```bash
# Terminal 1: Run API with watch
dotnet watch --project src/IoTSpy.Api

# Terminal 2: In another window, run tests
dotnet test --watch
```

Changes to `.cs` files auto-compile and restart the API.

### Frontend
```bash
cd frontend
npm run dev
```

Changes to `.tsx`/`.ts`/`.css` files hot-reload in browser.

---

## Submitting a PR

1. **Branch name:** `claude/<feature>-X39rc` or match the existing convention
2. **Commit message:** Clear, concise, past tense ("Added...", "Fixed...", "Updated...")
3. **Run tests locally:** `dotnet test` + `npm test` (frontend)
4. **Update docs:** README.md, ARCHITECTURE.md, PHASES-*.md as needed
5. **No force pushes** unless explicitly authorized
6. **Reference issues:** Link to related GitHub issues if applicable

---

## Key Gotchas

1. **Don't modify `IoTSpy.Core` without a reason** — It's the heart of the domain layer; keep it lean and dep-free
2. **Always use scoped repos in controllers** — Never pass a repo to a hosted service without creating a new scope
3. **Test migrations on SQLite AND Postgres locally** if you can — Some SQL dialects differ
4. **SignalR must use `JsonStringEnumConverter`** on both `AddControllers()` and `AddSignalR()` — Missing one causes numeric enum serialization
5. **Audit log is immutable by design** — write-once enforced at the DB level (`AuditWriteOnceTrigger` migration); deletions still allowed via `DataRetentionService`'s configurable retention policies (`GET/PUT /api/admin/retention` + DatabaseTab UI, already shipped)
6. **Rule priority matters** — Rules execute in ascending priority; test rule interactions
7. **Backend decompression happens pre-storage** — Captured body is decompressed; original compressed bytes forwarded to client
8. **Packet capture uses ring buffer** — 10k packet limit; old packets drop on buffer overflow (not a database transaction)

---

## Documentation Index

- **README.md** — Quick start, feature list, API reference
- **docs/ARCHITECTURE.md** — Technical architecture, project structure, data flow
- **docs/PLAN-INDEX.md** — High-level overview and navigation hub
- **docs/PHASES-COMPLETED.md** — All implemented phases (1-16, 18-22)
- **docs/PHASES-ARCHIVED.md** — Formally deprioritized phases (17 — non-IP/hardware-dependent protocols)
- **docs/PHASES-ROADMAP.md** — Future enhancement areas (no numbered phases remaining)
- **docs/CODE-REVIEW-FINDINGS.md** — Closed/archived: the 2026-05-09 multi-angle review, every item completed or deliberately deferred (with rationale) as of 2026-09-17. Historical record, not a board to pick work from.
- **docs/RUNBOOK.md** — On-call incident response for production deployments (proxy outages, SQLite corruption, JWT rotation, cert issues, retention/disk growth, SignalR degradation, container rollback)
- **docs/DESIGN-DECISIONS.md** — Architecture decisions, naming, implementation notes
- **docs/GAPS.md** — Known issues, technical debt, testing gaps
- **docs/AGENT-NOTES.md** — This file; Claude Code session setup and procedures

---

## Need Help?

1. **Architecture questions** — See `docs/ARCHITECTURE.md` and `docs/DESIGN-DECISIONS.md`
2. **What to build next** — `docs/CODE-REVIEW-FINDINGS.md` is fully closed (see its three deferred items — #48, #56, #59 — if picking one of those back up); otherwise check `docs/GAPS.md` and `docs/PHASES-ROADMAP.md` for open work and larger unnumbered future enhancement areas
3. **Known issues** — See `docs/GAPS.md` for open bugs and technical debt
4. **Phase details** — `docs/PHASES-COMPLETED.md` has full descriptions with test counts
5. **Command reference** — Check CLAUDE.md in the repo root

Good luck! 🚀
