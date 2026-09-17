import { useState, useCallback, useEffect } from 'react'
import { useSessions, useSessionDetail } from '../../hooks/useSessions'
import PresenceIndicator from './PresenceIndicator'
import AnnotationPanel from './AnnotationPanel'
import ConfirmDialog from '../common/ConfirmDialog'
import {
  createSession,
  deleteSession,
  generateShareToken,
  revokeShareToken,
  exportSession,
  addCaptureToSession,
} from '../../api/sessions'
import { downloadSessionHtmlReport, downloadSessionPdfReport } from '../../api/reports'
import { listCaptures } from '../../api/captures'
import type { CaptureAnnotation } from '../../types/sessions'
import type { CapturedRequestSummary } from '../../types/api'
import '../../styles/sessions.css'

type SessionTab = 'captures' | 'annotations' | 'activity'

export default function SessionsPanel() {
  const { sessions, loading, error, reload } = useSessions()
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null)
  const [activeTab, setActiveTab] = useState<SessionTab>('captures')
  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [newDesc, setNewDesc] = useState('')
  const [busy, setBusy] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [shareUrl, setShareUrl] = useState<string | null>(null)
  const [selectedCaptureId, setSelectedCaptureId] = useState<string | null>(null)

  // Confirmation dialog state
  const [confirmDelete, setConfirmDelete] = useState<{ id: string; name: string } | null>(null)
  const [confirmRevoke, setConfirmRevoke] = useState(false)

  // Capture picker state
  const [showCapturePicker, setShowCapturePicker] = useState(false)
  const [pickerCaptures, setPickerCaptures] = useState<CapturedRequestSummary[]>([])
  const [pickerLoading, setPickerLoading] = useState(false)

  const {
    session,
    captures,
    annotations,
    activity,
    presence,
    setAnnotations,
    reload: reloadDetail,
  } = useSessionDetail(activeSessionId)

  // Clear per-session state when switching sessions
  useEffect(() => {
    setShareUrl(null)
    setSelectedCaptureId(null)
    setShowCapturePicker(false)
    setPickerCaptures([])
  }, [activeSessionId])

  const showToast = useCallback((msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(null), 4000)
  }, [])

  const handleCreate = async () => {
    if (!newName.trim()) return
    setBusy(true)
    try {
      await createSession(newName.trim(), newDesc.trim() || undefined)
      setNewName('')
      setNewDesc('')
      setShowCreate(false)
      void reload()
    } catch {
      showToast('Failed to create session')
    } finally {
      setBusy(false)
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deleteSession(id)
      if (activeSessionId === id) setActiveSessionId(null)
      void reload()
    } catch {
      showToast('Failed to close session')
    }
  }

  // True when the Web Share API is available (requires HTTPS / localhost on iOS Safari).
  const canNativeShare = typeof navigator.share === 'function'

  const handleShare = async () => {
    if (!activeSessionId) return
    try {
      const { url } = await generateShareToken(activeSessionId)
      setShareUrl(url)
      void reloadDetail()
      void reload()

      if (canNativeShare) {
        try {
          await navigator.share({ title: session?.name ?? 'Investigation Session', url })
          return
        } catch (err) {
          if ((err as Error).name === 'AbortError') return
        }
      }
    } catch {
      showToast('Failed to generate share link')
    }
  }

  const handleNativeShare = () => {
    if (!shareUrl || !canNativeShare) return
    navigator.share({ title: session?.name ?? 'Investigation Session', url: shareUrl })
      .catch(err => {
        if ((err as Error).name !== 'AbortError') {
          void navigator.clipboard.writeText(shareUrl).catch(() => {})
          showToast('URL copied to clipboard')
        }
      })
  }

  const handleCopyShareUrl = () => {
    if (!shareUrl) return
    void navigator.clipboard.writeText(shareUrl).catch(() => {})
    showToast('URL copied')
  }

  const handleRevokeShare = async () => {
    if (!activeSessionId) return
    try {
      await revokeShareToken(activeSessionId)
      setShareUrl(null)
      showToast('Share link revoked')
      void reloadDetail()
      void reload()
    } catch {
      showToast('Failed to revoke share link')
    }
  }

  const handleExport = async () => {
    if (!activeSessionId || !session) return
    try {
      await exportSession(activeSessionId, session.name)
    } catch {
      showToast('Export failed')
    }
  }

  const handleReport = async (format: 'html' | 'pdf') => {
    if (!activeSessionId) return
    try {
      await (format === 'html' ? downloadSessionHtmlReport : downloadSessionPdfReport)(activeSessionId)
    } catch {
      showToast('Failed to generate report')
    }
  }

  const handleAnnotationAdded = useCallback((ann: CaptureAnnotation) => {
    setAnnotations(prev => [ann, ...prev])
  }, [setAnnotations])

  const handleAnnotationDeleted = useCallback((id: string) => {
    setAnnotations(prev => prev.filter(a => a.id !== id))
  }, [setAnnotations])

  const handleOpenCapturePicker = async () => {
    setShowCapturePicker(true)
    if (pickerCaptures.length > 0) return
    setPickerLoading(true)
    try {
      const result = await listCaptures({ page: 1, pageSize: 50 })
      setPickerCaptures(result.items)
    } catch {
      showToast('Failed to load captures')
    } finally {
      setPickerLoading(false)
    }
  }

  const handleAddCapture = async (captureId: string) => {
    if (!activeSessionId) return
    if (captures.some(c => c.captureId === captureId)) {
      showToast('Already in this session')
      return
    }
    try {
      await addCaptureToSession(activeSessionId, captureId)
      setShowCapturePicker(false)
      void reloadDetail()
      showToast('Capture added')
    } catch {
      showToast('Failed to add capture')
    }
  }

  const handleCaptureRowClick = (captureId: string) => {
    setSelectedCaptureId(captureId)
    setActiveTab('annotations')
  }

  return (
    <div className="sessions-panel">
      {toast && <div className="toast toast--info">{toast}</div>}

      {/* Confirmation dialogs */}
      {confirmDelete && (
        <ConfirmDialog
          title="Close session"
          message={`Close "${confirmDelete.name}"? This will remove the session for all collaborators.`}
          confirmLabel="Close Session"
          danger
          onConfirm={() => { void handleDelete(confirmDelete.id); setConfirmDelete(null) }}
          onCancel={() => setConfirmDelete(null)}
        />
      )}
      {confirmRevoke && (
        <ConfirmDialog
          title="Revoke share link"
          message="Revoke the share link? Anyone with the current link will lose access immediately."
          confirmLabel="Revoke"
          danger
          onConfirm={() => { void handleRevokeShare(); setConfirmRevoke(false) }}
          onCancel={() => setConfirmRevoke(false)}
        />
      )}

      {/* ── Sessions sidebar ─────────────────────────────────────────────────── */}
      <div className="sessions-panel__sidebar">
        <div className="sessions-panel__sidebar-header">
          <h3 className="sessions-panel__title">Sessions</h3>
          <button className="btn btn--primary btn--sm" onClick={() => setShowCreate(true)}>
            + New
          </button>
        </div>

        {showCreate && (
          <div className="sessions-panel__create-form">
            <input
              className="sessions-panel__input"
              placeholder="Session name"
              value={newName}
              onChange={e => setNewName(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && handleCreate()}
              autoFocus
            />
            <textarea
              className="sessions-panel__input"
              placeholder="Description (optional)"
              value={newDesc}
              onChange={e => setNewDesc(e.target.value)}
              rows={2}
            />
            <div className="sessions-panel__create-actions">
              <button className="btn btn--primary btn--sm" onClick={handleCreate} disabled={busy || !newName.trim()}>
                {busy ? 'Creating…' : 'Create'}
              </button>
              <button className="btn btn--secondary btn--sm" onClick={() => setShowCreate(false)}>Cancel</button>
            </div>
          </div>
        )}

        {loading && <p className="sessions-panel__loading">Loading…</p>}
        {error && <p className="sessions-panel__error">{error}</p>}

        <ul className="sessions-panel__list">
          {sessions.map(s => (
            <li
              key={s.id}
              className={`sessions-panel__item${activeSessionId === s.id ? ' sessions-panel__item--active' : ''}`}
              onClick={() => setActiveSessionId(s.id)}
            >
              <div className="sessions-panel__item-name">
                {s.name}
                {s.hasShareToken && (
                  <svg
                    className="sessions-panel__item-link-icon"
                    width="12" height="12" viewBox="0 0 24 24"
                    fill="none" stroke="currentColor" strokeWidth="2.5"
                    aria-label="Has share link"
                  >
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
                  </svg>
                )}
              </div>
              <div className="sessions-panel__item-meta">
                {s.createdByUsername} · {new Date(s.createdAt).toLocaleDateString()}
              </div>
              <button
                className="sessions-panel__item-close"
                onClick={e => { e.stopPropagation(); setConfirmDelete({ id: s.id, name: s.name }) }}
                title="Close session"
              >
                ×
              </button>
            </li>
          ))}
          {!loading && sessions.length === 0 && (
            <li className="sessions-panel__empty">
              No active sessions — create one to get started
            </li>
          )}
        </ul>
      </div>

      {/* ── Session detail ────────────────────────────────────────────────────── */}
      <div className="sessions-panel__detail">
        {!activeSessionId && (
          <div className="sessions-panel__placeholder">
            <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
              <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
              <circle cx="9" cy="7" r="4" />
              <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
              <path d="M16 3.13a4 4 0 0 1 0 7.75" />
            </svg>
            <p>Select a session to view details</p>
          </div>
        )}

        {activeSessionId && session && (
          <>
            <div className="sessions-panel__detail-header">
              <div>
                <h3 className="sessions-panel__detail-title">{session.name}</h3>
                {session.description && (
                  <p className="sessions-panel__detail-desc">{session.description}</p>
                )}
              </div>
              <PresenceIndicator presence={presence} />
              <div className="sessions-panel__detail-actions">
                <button className="btn btn--sm btn--secondary" onClick={handleExport} title="Export session as ZIP">
                  Export
                </button>
                <button className="btn btn--sm btn--secondary" onClick={() => handleReport('html')} title="Generate HTML report">
                  Report (HTML)
                </button>
                <button className="btn btn--sm btn--secondary" onClick={() => handleReport('pdf')} title="Generate PDF report">
                  Report (PDF)
                </button>
                <button className="btn btn--sm btn--secondary" onClick={handleShare}>
                  {session.hasShareToken ? 'Copy Link' : 'Share'}
                </button>
                {session.hasShareToken && (
                  <button className="btn btn--sm btn--danger" onClick={() => setConfirmRevoke(true)}>
                    Revoke
                  </button>
                )}
              </div>
            </div>

            {/* Share URL bar */}
            {shareUrl && (
              <div className="sessions-panel__share-bar">
                <a
                  className="sessions-panel__share-bar-url"
                  href={shareUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  title="Tap to open in browser"
                >
                  {shareUrl}
                </a>
                <div className="sessions-panel__share-bar-actions">
                  <button className="btn btn--sm btn--secondary" onClick={handleCopyShareUrl}>Copy</button>
                  {canNativeShare && (
                    <button className="btn btn--sm btn--primary" onClick={handleNativeShare}>
                      Share / AirDrop
                    </button>
                  )}
                </div>
                {!canNativeShare && (
                  <p className="sessions-panel__share-hint">
                    Tap the link above to open in browser, then use the browser Share button for AirDrop
                  </p>
                )}
              </div>
            )}

            {/* Tabs */}
            <div className="sessions-panel__tabs">
              {(['captures', 'annotations', 'activity'] as const).map(tab => (
                <button
                  key={tab}
                  className={`sessions-panel__tab${activeTab === tab ? ' sessions-panel__tab--active' : ''}`}
                  onClick={() => setActiveTab(tab)}
                >
                  {tab === 'captures' ? `Captures (${captures.length})` :
                   tab === 'annotations' ? `Annotations (${annotations.length})` :
                   'Activity'}
                </button>
              ))}
            </div>

            {/* Captures tab */}
            {activeTab === 'captures' && (
              <div className="sessions-panel__captures">
                <div className="sessions-panel__captures-header">
                  <button className="btn btn--sm btn--secondary" onClick={handleOpenCapturePicker}>
                    + Add Capture
                  </button>
                </div>

                {/* Inline capture picker */}
                {showCapturePicker && (
                  <div className="sessions-panel__capture-picker">
                    <div className="sessions-panel__capture-picker-header">
                      <span>Select capture to add</span>
                      <button
                        className="sessions-panel__capture-picker-close"
                        onClick={() => setShowCapturePicker(false)}
                      >
                        ×
                      </button>
                    </div>
                    {pickerLoading && <p className="sessions-panel__empty">Loading…</p>}
                    {!pickerLoading && pickerCaptures.length === 0 && (
                      <p className="sessions-panel__empty">No captures available.</p>
                    )}
                    <div style={{ overflowY: 'auto', flex: 1 }}>
                      {pickerCaptures.map(c => {
                        const alreadyAdded = captures.some(sc => sc.captureId === c.id)
                        return (
                          <div
                            key={c.id}
                            className={`sessions-panel__picker-row${alreadyAdded ? ' sessions-panel__picker-row--added' : ''}`}
                            onClick={() => !alreadyAdded && void handleAddCapture(c.id)}
                            title={alreadyAdded ? 'Already in session' : `${c.method} ${c.host}${c.path}`}
                          >
                            <span className={`badge badge--method-${c.method.toLowerCase()}`}>{c.method}</span>
                            <span className="sessions-panel__capture-host">{c.host}</span>
                            <span className="sessions-panel__capture-path">{c.path || '/'}</span>
                            {alreadyAdded && <span className="sessions-panel__picker-added">✓</span>}
                          </div>
                        )
                      })}
                    </div>
                  </div>
                )}

                {!showCapturePicker && captures.length === 0 && (
                  <p className="sessions-panel__empty">
                    No captures in this session yet. Use "+ Add Capture" above.
                  </p>
                )}
                {captures.map(sc => (
                  <div
                    key={sc.id}
                    className={`sessions-panel__capture-row${selectedCaptureId === sc.captureId ? ' sessions-panel__capture-row--selected' : ''}`}
                    onClick={() => handleCaptureRowClick(sc.captureId)}
                    title="Click to annotate"
                  >
                    {sc.capture ? (
                      <>
                        <span className={`badge badge--method-${sc.capture.method.toLowerCase()}`}>
                          {sc.capture.method}
                        </span>
                        <span className="sessions-panel__capture-host">{sc.capture.host}</span>
                        <span className="sessions-panel__capture-path">{sc.capture.path || '/'}</span>
                        <span className={`badge badge--status-${Math.floor(sc.capture.statusCode / 100)}xx`}>
                          {sc.capture.statusCode}
                        </span>
                        <span className="sessions-panel__capture-time">
                          {new Date(sc.capture.timestamp).toLocaleTimeString()}
                        </span>
                      </>
                    ) : (
                      <span className="sessions-panel__capture-id">{sc.captureId}</span>
                    )}
                  </div>
                ))}
              </div>
            )}

            {/* Annotations tab */}
            {activeTab === 'annotations' && (
              <>
                {captures.length === 0 ? (
                  <p className="sessions-panel__empty" style={{ margin: 'var(--space-4)' }}>
                    Add captures to this session first, then click a capture to annotate it.
                  </p>
                ) : !selectedCaptureId && captures.length > 0 ? (
                  <p className="sessions-panel__empty" style={{ margin: 'var(--space-4)' }}>
                    Go to the Captures tab and click a capture to annotate it.
                  </p>
                ) : (
                  <AnnotationPanel
                    sessionId={activeSessionId}
                    captureId={selectedCaptureId ?? captures[0].captureId}
                    annotations={annotations}
                    onAdded={handleAnnotationAdded}
                    onDeleted={handleAnnotationDeleted}
                  />
                )}
              </>
            )}

            {/* Activity feed tab */}
            {activeTab === 'activity' && (
              <div className="sessions-panel__activity">
                {activity.length === 0 && (
                  <p className="sessions-panel__empty">No activity yet.</p>
                )}
                {activity.map(a => (
                  <div key={a.id} className="sessions-panel__activity-row">
                    <span className="sessions-panel__activity-user">{a.username}</span>
                    <span className="sessions-panel__activity-action">{a.action}</span>
                    {a.details && (
                      <span className="sessions-panel__activity-details">{a.details}</span>
                    )}
                    <span className="sessions-panel__activity-time">
                      {new Date(a.timestamp).toLocaleTimeString()}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
