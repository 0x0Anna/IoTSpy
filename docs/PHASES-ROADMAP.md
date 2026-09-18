# IoTSpy — Roadmap & Future Phases

This document covers planned future work.

See [PHASES-COMPLETED.md](PHASES-COMPLETED.md) for all completed work including phases 1–16, 18–22, API & Backend Polish, and Frontend Usability enhancements.

> Phase 17 (Non-IP IoT protocol expansion) has been formally archived. See [PHASES-ARCHIVED.md](PHASES-ARCHIVED.md).

---

## Future Enhancement Areas

### Scanner & Anomaly
- **Scan findings correlation** — Client-side grouping by type/CVE/service *within* a single scan job's results shipped (`ScanFindingsView`, frontend-only); cross-scan-job / cross-device correlation ("this CVE affects 4 devices") is still open — there is no fleet-wide findings query surface today (`ScannerController` only exposes `jobs/{id}/findings`)
- **Custom anomaly rules** — Declarative anomaly rules (similar to the manipulation rules engine) to flag specific traffic patterns; replaces purely statistical Welford baseline
- **Behavioral fingerprinting** — Host-level Welford baselines (duration/size/status-code) now persist across restarts (`HostBaselineCheckpointService` + `HostBaselines` table — see `docs/GAPS.md` Design Assumptions #5). Still open: device-level (rather than host-level) keying, and detecting *changes* in device communication patterns over time (pattern-drift analysis over the persisted history) — this PR only persists and restores the existing point-in-time statistical baseline, it doesn't analyze how that baseline drifts
- **Behavioral inference / privacy-leakage module** — Infer occupant activity/routines from packet metadata alone (below TLS); see [PLAN-BEHAVIORAL-INFERENCE.md](PLAN-BEHAVIORAL-INFERENCE.md) for the full design

### Protocol Decoder Depth
- **DNS DNSSEC validation** — Validate DNSSEC chains (DoH/DoT *detection* shipped — see `docs/CODE-REVIEW-FINDINGS.md` #44 / `DohDetector`+`DotDetector`; full DNSSEC chain validation is still open)

### Longer-Horizon
- **Offline mode** — Cache captures, rules, and playback without network connectivity
- **Mobile app** — Native iOS/Android for field reconnaissance and live monitoring
- **Machine learning anomaly detection** — Replace Welford statistical baseline with trained ML models
- **Custom protocol decoders** — User-defined binary protocol parsers via plugin scripting
- **Enterprise features** — RBAC refinement, data classification, compliance reporting (GDPR, HIPAA), encryption at rest
- **Multi-tenant** — Organizational namespaces, resource quotas, billing integration
