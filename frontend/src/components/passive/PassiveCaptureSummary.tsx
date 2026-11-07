import { useCallback, useEffect, useState } from 'react'
import '../../styles/passive.css'
import type { PassiveCaptureSummary as Summary, PassiveCaptureSession } from '../../types/api'
import { useProxy } from '../../hooks/useProxy'
import {
  clearBuffer,
  clearDeviceFilter,
  deletePassiveSession,
  getPassiveSummary,
  listPassiveSessions,
  savePassiveSession,
  setDeviceFilter,
} from '../../api/passiveCapture'

type Tab = 'summary' | 'sessions'

interface Props {
  embedded?: boolean
}

export default function PassiveCaptureSummary({ embedded = false }: Props) {
  const proxy = useProxy()
  const settings = proxy.status?.settings ?? null
  const isPassive = settings?.isPassive ?? false
  const isRunning = proxy.status?.isRunning ?? false
  const isPassiveActive = isPassive && isRunning

  const [tab, setTab] = useState<Tab>('summary')
  const [summary, setSummary] = useState<Summary | null>(null)
  const [sessions, setSessions] = useState<PassiveCaptureSession[]>([])
  const [loading, setLoading] = useState(false)
  const [modeLoading, setModeLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [filterInput, setFilterInput] = useState('')
  const [infoOpen, setInfoOpen] = useState(false)
  const [saveDialogOpen, setSaveDialogOpen] = useState(false)
  const [saveName, setSaveName] = useState('')
  const [saveDesc, setSaveDesc] = useState('')
  const [saveClear, setSaveClear] = useState(false)

  // Passive is a flag on top of the transport mode (Explicit / Gateway Redirect / ARP Spoof),
  // so toggling it never changes how traffic reaches the proxy.
  // Proxy lifecycle (start/stop) stays in the header.
  async function handleSetPassive(passive: boolean) {
    setModeLoading(true)
    setError(null)
    try {
      await proxy.saveSettings({ isPassive: passive })
      if (!passive) setSummary(null)
    } catch {
      setError('Failed to switch mode.')
    } finally {
      setModeLoading(false)
    }
  }

  const loadSummary = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const s = await getPassiveSummary()
      setSummary(s)
      setFilterInput(s.activeDeviceFilter.join(', '))
    } catch {
      setError('Failed to load passive capture summary.')
    } finally {
      setLoading(false)
    }
  }, [])

  const loadSessions = useCallback(async () => {
    try {
      setSessions(await listPassiveSessions())
    } catch {
      setError('Failed to load sessions.')
    }
  }, [])

  useEffect(() => {
    loadSummary()
    loadSessions()
    const interval = setInterval(loadSummary, 5000)
    return () => clearInterval(interval)
  }, [loadSummary, loadSessions])

  // ── Filter & buffer ────────────────────────────────────────────────────────

  async function handleApplyFilter() {
    const ips = filterInput.split(',').map((s) => s.trim()).filter(Boolean)
    try {
      if (ips.length === 0) await clearDeviceFilter()
      else await setDeviceFilter(ips)
      await loadSummary()
    } catch {
      setError('Failed to update device filter.')
    }
  }

  async function handleClearBuffer() {
    await clearBuffer()
    await loadSummary()
  }

  async function handleSaveSession() {
    if (!saveName.trim()) return
    const ips = filterInput.split(',').map((s) => s.trim()).filter(Boolean)
    try {
      await savePassiveSession(saveName, saveDesc || undefined, ips.length > 0 ? ips : undefined, saveClear)
      setSaveDialogOpen(false)
      setSaveName('')
      setSaveDesc('')
      await loadSessions()
      await loadSummary()
    } catch {
      setError('Failed to save session.')
    }
  }

  async function handleDeleteSession(id: string) {
    await deletePassiveSession(id)
    await loadSessions()
  }

  const maxCount = summary?.topEndpoints[0]?.count ?? 1

  return (
    <div className={`passive-summary${embedded ? ' passive-summary--embedded' : ''}`}>
      {/* ── Mode toggle bar ──────────────────────────────────────────────── */}
      <div className="passive-summary__mode-bar">
        <div className="passive-summary__mode-status">
          <span
            className={`passive-summary__mode-dot ${
              isPassive && isRunning  ? 'passive-summary__mode-dot--active' :
              !isPassive && isRunning ? 'passive-summary__mode-dot--running' :
              isPassive               ? 'passive-summary__mode-dot--idle-passive' :
                                        'passive-summary__mode-dot--idle-active'
            }`}
          />
          <span className="passive-summary__mode-label">
            {isPassiveActive
              ? 'Passive — observing, not recording'
              : isPassive && !isRunning
                ? 'Passive — proxy stopped'
                : !isPassive && isRunning
                  ? 'Active — recording traffic to the database'
                  : 'Active — proxy stopped'}
          </span>
          {isPassiveActive && summary && (
            <span className="passive-summary__mode-count">
              {summary.totalRequests.toLocaleString()} buffered
            </span>
          )}
        </div>

        <div className="passive-summary__mode-toggle">
          <button
            className={`passive-summary__mode-btn${!isPassive ? ' passive-summary__mode-btn--active' : ''}`}
            onClick={() => !isPassive || handleSetPassive(false)}
            disabled={modeLoading || !isPassive}
          >
            Active
          </button>
          <button
            className={`passive-summary__mode-btn${isPassive ? ' passive-summary__mode-btn--active' : ''}`}
            onClick={() => isPassive || handleSetPassive(true)}
            disabled={modeLoading || isPassive}
          >
            Passive
          </button>
        </div>

        <div className="passive-summary__info-wrap">
          <button
            className="passive-summary__info-btn"
            onClick={() => setInfoOpen((o) => !o)}
            aria-label="About capture modes"
          >
            ⓘ
          </button>
          {infoOpen && (
            <div className="passive-summary__info-popover" role="tooltip">
              <button className="passive-summary__info-close" onClick={() => setInfoOpen(false)} aria-label="Close">✕</button>
              <strong>Active</strong> — all traffic is recorded to the database. Manipulation rules and anomaly detection run per request.
              <br /><br />
              <strong>Passive</strong> — traffic passes through unchanged, using the same proxy mode (explicit, gateway redirect or ARP spoof). No rules, breakpoints or tarpits apply. HTTPS is decrypted for observation when TLS interception is on, otherwise relayed untouched with only its handshake metadata recorded. Nothing is written to the database; observations are buffered in memory until you save a named session.
            </div>
          )}
        </div>
      </div>

      {/* ── Tab header — only needed when passive content is available ── */}
      {(!embedded || isPassive) && (
        <div className="passive-summary__header">
          <h2 className="passive-summary__title">Passive Capture</h2>
          <div className="passive-summary__tabs">
            {(['summary', 'sessions'] as Tab[]).map((t) => (
              <button
                key={t}
                className={`passive-summary__tab${tab === t ? ' passive-summary__tab--active' : ''}`}
                onClick={() => setTab(t)}
              >
                {t === 'summary' ? 'Live Summary' : 'Saved Sessions'}
              </button>
            ))}
          </div>
        </div>
      )}

      {error && <div className="passive-summary__error">{error}</div>}

      {tab === 'summary' && (
        <>
          {/* Device filter bar — only useful when capturing */}
          {isPassive && (
            <div className="passive-summary__filter-bar">
              <label className="passive-summary__label">Record only these device IPs (comma-separated):</label>
              <input
                className="passive-summary__input"
                value={filterInput}
                onChange={(e) => setFilterInput(e.target.value)}
                placeholder="e.g. 192.168.1.10, 192.168.1.20  (empty = all devices)"
              />
              <button className="passive-summary__btn" onClick={handleApplyFilter}>Apply</button>
              <button className="passive-summary__btn passive-summary__btn--secondary" onClick={handleClearBuffer}>
                Clear Buffer
              </button>
              {isPassiveActive && (
                <button className="passive-summary__btn passive-summary__btn--save" onClick={() => setSaveDialogOpen(true)}>
                  Save Session
                </button>
              )}
            </div>
          )}

          {loading && !summary && <div className="passive-summary__loading">Loading…</div>}

          {isPassiveActive && summary && (
            <div className="passive-summary__stats-grid">
              <div className="passive-summary__stat-card">
                <div className="passive-summary__stat-value">{summary.totalRequests.toLocaleString()}</div>
                <div className="passive-summary__stat-label">Buffered requests</div>
              </div>

              <div className="passive-summary__stat-card passive-summary__stat-card--wide">
                <div className="passive-summary__section-title">Top Hosts</div>
                <ul className="passive-summary__host-list">
                  {summary.topHosts.map((h) => (
                    <li key={h} className="passive-summary__host-item">{h}</li>
                  ))}
                  {summary.topHosts.length === 0 && (
                    <li className="passive-summary__empty">No traffic captured yet</li>
                  )}
                </ul>
              </div>

              <div className="passive-summary__stat-card">
                <div className="passive-summary__section-title">Status Codes</div>
                <ul className="passive-summary__code-list">
                  {summary.statusCodes.map((b) => (
                    <li key={b.statusCode} className="passive-summary__code-item">
                      <span
                        className={`passive-summary__code-badge passive-summary__code-badge--${Math.floor(b.statusCode / 100)}xx`}
                      >
                        {b.statusCode}
                      </span>
                      <span className="passive-summary__code-count">{b.count.toLocaleString()}</span>
                    </li>
                  ))}
                  {summary.statusCodes.length === 0 && (
                    <li className="passive-summary__empty">No responses yet</li>
                  )}
                </ul>
              </div>
            </div>
          )}

          {/* Endpoint frequency heatmap */}
          {isPassiveActive && summary && summary.topEndpoints.length > 0 && (
            <div className="passive-summary__endpoints">
              <div className="passive-summary__section-title">Endpoint Frequency</div>
              <div className="passive-summary__endpoint-list">
                {summary.topEndpoints.map((ep, i) => (
                  <div key={i} className="passive-summary__endpoint-row">
                    <span className="passive-summary__method">{ep.method}</span>
                    <span className="passive-summary__endpoint-path">{ep.host}{ep.path}</span>
                    <div className="passive-summary__bar-wrapper">
                      <div
                        className="passive-summary__bar"
                        style={{ width: `${Math.round((ep.count / maxCount) * 100)}%` }}
                      />
                    </div>
                    <span className="passive-summary__endpoint-count">{ep.count.toLocaleString()}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Save session dialog */}
          {saveDialogOpen && (
            <div className="passive-summary__dialog-overlay" onClick={() => setSaveDialogOpen(false)}>
              <div className="passive-summary__dialog" onClick={(e) => e.stopPropagation()}>
                <h3 className="passive-summary__dialog-title">Save Passive Session</h3>
                <label className="passive-summary__label">Name</label>
                <input
                  className="passive-summary__input"
                  value={saveName}
                  onChange={(e) => setSaveName(e.target.value)}
                  placeholder="Session name"
                  autoFocus
                />
                <label className="passive-summary__label">Description (optional)</label>
                <input
                  className="passive-summary__input"
                  value={saveDesc}
                  onChange={(e) => setSaveDesc(e.target.value)}
                  placeholder="Description"
                />
                <label className="passive-summary__label passive-summary__label--checkbox">
                  <input
                    type="checkbox"
                    checked={saveClear}
                    onChange={(e) => setSaveClear(e.target.checked)}
                  />
                  Clear buffer after saving
                </label>
                <div className="passive-summary__dialog-actions">
                  <button className="passive-summary__btn passive-summary__btn--save" onClick={handleSaveSession}>
                    Save
                  </button>
                  <button className="passive-summary__btn passive-summary__btn--secondary" onClick={() => setSaveDialogOpen(false)}>
                    Cancel
                  </button>
                </div>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'sessions' && (
        <div className="passive-summary__sessions">
          {sessions.length === 0 ? (
            <div className="passive-summary__empty-state">
              No saved sessions yet. Start a passive capture and use <strong>Save Session</strong> to persist a snapshot.
            </div>
          ) : (
            <table className="passive-summary__table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Captures</th>
                  <th>Device Filter</th>
                  <th>Created</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {sessions.map((s) => (
                  <tr key={s.id}>
                    <td>{s.name}{s.description && <div className="passive-summary__session-desc">{s.description}</div>}</td>
                    <td>{s.entryCount.toLocaleString()}</td>
                    <td>{s.deviceFilter ?? 'All devices'}</td>
                    <td>{new Date(s.createdAt).toLocaleString()}</td>
                    <td>
                      <button
                        className="passive-summary__btn passive-summary__btn--danger"
                        onClick={() => handleDeleteSession(s.id)}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}
