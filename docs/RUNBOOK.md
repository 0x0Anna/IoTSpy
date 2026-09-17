# IoTSpy — On-Call Runbook

Operational incident response for a running IoTSpy deployment. This is **not** a developer setup guide — for build/test/dev-environment issues, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

Closes `docs/CODE-REVIEW-FINDINGS.md` #54.

---

## Table of Contents
1. [Health checks & where to look first](#health-checks--where-to-look-first)
2. [Proxy stopped intercepting traffic](#proxy-stopped-intercepting-traffic)
3. [Recovering from a corrupt SQLite database](#recovering-from-a-corrupt-sqlite-database)
4. [Rotating the JWT secret](#rotating-the-jwt-secret)
5. [Root CA / certificate issues](#root-ca--certificate-issues)
6. [Retention / disk growth](#retention--disk-growth)
7. [SignalR / real-time features degraded](#signalr--real-time-features-degraded)
8. [Container restart & rollback](#container-restart--rollback)

---

## Health checks & where to look first

- `GET /health` — liveness (process up, DB reachable). Wired via `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` in `Program.cs`.
- `GET /ready` — readiness (safe to receive traffic).
- `GET /metrics` — Prometheus scrape endpoint (`prometheus-net.AspNetCore`). See `src/IoTSpy.Api/Services/IoTSpyMetrics.cs` for the metric catalog and `deploy/grafana/dashboards/` for the bundled dashboards.
- Logs: Serilog to console + rolling file sink (see `Program.cs`'s `UseSerilog` config) and optionally Seq if `Serilog.Sinks.Seq` is configured. In Kubernetes, `kubectl logs` the pod; in `docker-compose.prod.yml`, `docker compose logs -f iotspy-api`.
- Audit trail: `GET /api/admin/audit` (Admin-only) — every mutating action (rule changes, cert regeneration, backup/restore, retention changes) is recorded with before/after diffs. Check this first when something changed unexpectedly and you don't know who/what did it.

---

## Proxy stopped intercepting traffic

Symptoms: no new rows in Captures, dashboard traffic view goes quiet, devices show as active but nothing is being logged.

1. **Confirm the API process is actually up.** `GET /health` — if this fails, it's a process/infra problem, not a proxy-specific one; see [Container restart & rollback](#container-restart--rollback).
2. **Check which proxy mode is configured** (`ProxySettings` row, `Id=1` — single-row settings table). Explicit proxy (`ExplicitProxyServer`), transparent proxy (`TransparentProxyServer`), and MQTT broker proxy (`MqttBrokerProxy`) are separate long-lived singleton listeners started at app startup — a crash in one doesn't necessarily take down the others. Check `GET /api/admin/stats` or the Admin dashboard for per-listener status if exposed; otherwise check logs for a listener-specific exception at startup (`Program.cs` registers these via `AddHostedService`).
3. **Explicit proxy**: devices need to be configured to actually send traffic through the configured proxy port. Confirm the device's proxy config didn't get reset (common after a device reboot or app-level factory reset on IoT devices).
4. **Transparent proxy**: this depends on OS-level traffic redirection (iptables/nftables rules or a router configured to redirect to this host). Confirm those rules survived a host reboot — they are **not** managed by IoTSpy itself and don't persist automatically unless your deployment scripts reapply them.
5. **TLS interception silently failing**: if devices pin the IoTSpy root CA and something regenerated it (see [Root CA / certificate issues](#root-ca--certificate-issues)), devices will fail TLS handshakes and traffic simply won't flow — check device-side connection error logs if you have any, or watch for a spike in `TlsPassthrough`-classified captures (indicates MITM is failing and traffic is falling back to passthrough) via the `Protocol` filter on `GET /api/captures`.
6. **SharpPcap-based passive capture** (if in use): this requires `CAP_NET_RAW`/`CAP_NET_ADMIN` on the real `dotnet` binary (not a symlink) on Linux hosts — a host OS update or container image rebuild can silently drop this capability. See AGENT.md's "Linux packet capture (SharpPcap)" section for the `setcap` command; re-run it and restart the API after any base-image change.
7. **Scan scope / consent gate blocking activity you expected to see**: if `ScanScope`s are configured (`GET /api/scopes`), traffic *capture* is unaffected by this — it only gates active *scanning* (`ScannerController.StartScan` returns 403 for out-of-scope devices). Don't confuse "no new scan findings" with "proxy isn't capturing."

---

## Recovering from a corrupt SQLite database

Symptoms: `SqliteException` in logs mentioning "database disk image is malformed," API fails health checks, or `dotnet ef database update` fails.

1. **Stop the API first** — don't attempt repair against a live connection.
2. **Check for an automatic pre-restore safety backup.** `POST /api/admin/restore` always takes a safety-net backup of the live file before overwriting it (see `AdminController`) — if the corruption happened *during* a restore attempt, that safety copy is your fastest way back. Its filename/location is logged at restore time.
3. **Try SQLite's own recovery first**, on a copy of the file (never operate on the only copy):
   ```bash
   cp iotspy.db iotspy.db.bak
   sqlite3 iotspy.db.bak "PRAGMA integrity_check;"
   ```
   If it reports specific corruption, attempt the dump/reload recovery:
   ```bash
   sqlite3 iotspy.db.bak ".recover" | sqlite3 iotspy-recovered.db
   ```
4. **If recovery fails or data loss is unacceptable, restore from a scheduled backup.** `GET /api/admin/backup` (Admin-only) produces a `VACUUM INTO`-based consistent snapshot — if you have automated backups hitting this endpoint on a schedule, restore the most recent one via `POST /api/admin/restore` (validates the SQLite magic header before accepting the upload).
5. **If no backup exists and recovery fails**, the pragmatic path is: stop the API, replace `iotspy.db` with a fresh one (`dotnet ef database update` against an empty file recreates schema at the current migration), and accept the data loss. Captures/scans are operational data, not the source of truth for anything external — there's no cross-system reconciliation needed, but you will lose device history, capture history, and audit trail up to the last good backup.
6. **Postgres deployments**: the backup/restore endpoints return 501 for Postgres (shelling out to `pg_dump`/`pg_restore` was deliberately not built into the app — see `docs/CODE-REVIEW-FINDINGS.md` #42's resolution notes). Use your normal Postgres backup/restore tooling (`pg_dump`/`pg_restore`, or your managed database provider's point-in-time recovery) instead.
7. **After recovery**: check `GET /health` and `GET /api/admin/audit` (if the audit table itself survived) to confirm data continuity, and watch logs for any `AlterColumn`/migration-mismatch errors if the recovered file predates the currently-deployed migration set — run `dotnet ef database update` to catch it up if so.

---

## Rotating the JWT secret

**Important limitation, stated plainly: there is currently no way to rotate `Auth:JwtSecret` without invalidating every outstanding session.** The app validates tokens against a single symmetric key (`Program.cs`, `IssuerSigningKey = new SymmetricSecurityKey(...)` built from the one configured secret) — there is no dual-key grace-period support today. Anyone claiming otherwise for this version of the app is wrong; don't schedule a rotation expecting seamless continuity until that's built.

**Recommended procedure given that constraint:**
1. Schedule rotation during a maintenance window / low-traffic period, and notify users they'll need to log back in.
2. Generate a new secret (≥ 32 characters — enforced at startup, see `Program.cs`'s `InvalidOperationException` checks).
3. Update the secret in whatever store your deployment uses:
   - Local dev: `dotnet user-secrets set "Auth:JwtSecret" "<new-secret>" --project src/IoTSpy.Api`
   - Docker Compose: update the `Auth__JwtSecret` environment variable in `docker-compose.prod.yml` (or its `.env` file) and recreate the container.
   - Helm: update the `secret.yaml`-backed value (check `deploy/helm/iotspy/templates/secret.yaml` for how it's sourced — typically a `values.yaml` override or an externally-managed `Secret` reference) and roll the deployment.
4. Restart/redeploy the API. All existing JWTs become invalid immediately (they fail `ValidateIssuerSigningKey`); every logged-in user is signed out and must log back in against the new secret.
5. If seamless rotation becomes a real requirement, the fix is to accept a list of valid signing keys during a transition window (validate against old-or-new, issue only new) — that's a code change, not an operational workaround, and isn't implemented as of this writing.

---

## Root CA / certificate issues

Symptoms: devices suddenly fail TLS handshakes against the MITM proxy; `TlsPassthrough`-classified captures spike where MITM captures used to appear.

1. **Check whether the root CA was recently regenerated.** `POST /api/certificates/root-ca/regenerate` is an explicit, audited action (`AuditEntry` with `Action = "RegenerateRootCA"`) — check `GET /api/admin/audit` for who/when. Regenerating the CA invalidates trust on every device that previously installed the old CA cert; they all need to re-install the new one.
2. **After any root CA regeneration, delete existing leaf certificates** so they get reissued under the new CA — see AGENT.md's "iOS/macOS leaf cert constraints" section for the exact SQL if this wasn't done automatically as part of the regenerate flow.
3. **iOS/macOS clients specifically**: leaf certs have a ≤397-day validity ceiling and specific AKI/SAN formatting requirements (see AGENT.md) — a cert that validates fine on Android/Linux clients can still be silently rejected by Apple's stricter validation. If only Apple devices are affected, check leaf cert validity period and SAN format before assuming a broader CA problem.
4. **Re-distribute the new root CA** to all intercepted devices — this is a manual, per-device step outside the app (device profile install / trust store update); IoTSpy doesn't manage device-side trust stores.

---

## Retention / disk growth

Symptoms: disk usage climbing, `iotspy.db` growing without bound, `GET /api/admin/stats`'s `database.estimatedSizeBytes` trending up unexpectedly.

1. **Confirm retention is actually enabled.** `GET /api/admin/retention` — `DataRetentionOptions.Enabled` defaults to `false`; if nobody explicitly turned it on, nothing is being purged, by design.
2. **Check each tier's window.** Captures, packets, scan jobs, OpenRTB events, and protocol messages each have an independent `*RetentionDays` setting (0 = never purge that tier) — see the Admin Database tab (`DatabaseTab.tsx`) or `PUT /api/admin/retention` for the current values.
3. **Audit log has a separate two-stage policy** (archive-then-purge, not a single delete) — `AuditRetentionDays` moves old entries to `AuditArchive`, `AuditArchivePurgeDays` deletes from the archive. A large `AuditArchive` table with `AuditArchivePurgeDays = 0` will keep growing indefinitely even with primary retention enabled — this is a common "why is disk still growing" trap.
4. **SQLite doesn't shrink the file automatically** after deletes — `ExecuteDeleteAsync`-based retention passes free internal pages but don't return them to the OS. If a large one-time purge just ran and disk usage didn't drop, run `VACUUM` (the same operation `GET /api/admin/backup`'s `VACUUM INTO` uses internally, but applied in-place) during a maintenance window — this rewrites the whole file and requires roughly 2x the current file size in free disk space during the operation.
5. **Ring buffer capacity** (`PacketCapture:RingBufferCapacity`, default 10,000) bounds in-memory packet capture separately from DB retention — if raw packet capture (not proxied HTTP/MQTT captures) is the disk driver, check `PacketRetentionDays` specifically.

---

## SignalR / real-time features degraded

Symptoms: live capture stream stalls in the UI, collaboration presence indicators go stale, in-app alert toasts stop appearing.

1. **Check `iotspy_signalr_connections`** (Prometheus gauge, see `IoTSpyMetrics.cs`) — a sudden drop to zero with the API otherwise healthy suggests a hub-level exception or a reverse-proxy/load-balancer WebSocket-upgrade misconfiguration (SignalR needs WebSocket support end-to-end; a proxy silently downgrading to long-polling will work but degrades badly under load).
2. **Multi-replica deployments**: SignalR needs a shared backplane across replicas — this codebase uses `Microsoft.AspNetCore.SignalR.StackExchangeRedis`. If you're running more than one API replica and didn't configure Redis, cross-replica broadcast (alerts, presence, live captures) will only reach clients connected to the same replica that generated the event. Confirm the Redis backplane connection string is set and Redis itself is reachable.
3. **Token-in-query-string caveat**: SignalR auth passes the JWT as `?access_token=` on the connection URL (required by the SignalR JS client for WebSocket connections, which can't set custom headers). This means access tokens can land in proxy/load-balancer access logs — if you're debugging by grepping infra logs, be aware you may be looking at live bearer tokens; treat those log lines as sensitive and don't paste them into shared channels.

---

## Container restart & rollback

1. **Docker Compose** (`docker-compose.prod.yml`): `docker compose restart iotspy-api` for a simple restart; `docker compose pull && docker compose up -d` to roll forward to a newly pushed image. Rollback: re-tag/pull the previous known-good image tag and `up -d` again — this repo doesn't maintain a rollback script, so track your last-known-good tag externally (CI run number or git SHA tag) before rolling forward.
2. **Helm** (`deploy/helm/iotspy/`): `helm rollback iotspy <revision>` using `helm history iotspy` to find the last-good revision. Check `hpa.yaml` (HorizontalPodAutoscaler) isn't fighting a manual scale-down during incident response — pause autoscaling (or scale the HPA's min/max temporarily) if you need to hold replica count steady while debugging.
3. **After any restart**, re-verify `GET /health` and `GET /ready`, and check the Admin dashboard / audit log for the timeframe around the incident to confirm nothing silently failed during the restart window (e.g. a scheduled scan or retention pass that was mid-flight).
4. **Database migrations on rollback**: rolling back the app image to an older version while the database is on a newer migration is unsupported — EF Core migrations in this codebase are forward-only (no `Down()` methods maintained as a rollback path in practice). If you must roll back the app, restore the database from a pre-migration backup as well, or accept that the older app version may not tolerate the newer schema.
