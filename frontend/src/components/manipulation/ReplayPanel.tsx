import { useState } from 'react'
import ConfirmDialog from '../common/ConfirmDialog'
import type { ReplaySession, CreateReplayRequest, CapturedRequestSummary } from '../../types/api'
import '../../styles/manipulation.css'

interface Props {
  replays: ReplaySession[]
  loading: boolean
  error: string | null
  captures: CapturedRequestSummary[]
  onReplay: (req: CreateReplayRequest) => Promise<ReplaySession | null>
  onDelete: (id: string) => void
}

export default function ReplayPanel({ replays, loading, error, captures, onReplay, onDelete }: Props) {
  const [selectedCaptureId, setSelectedCaptureId] = useState('')
  const [method, setMethod] = useState('')
  const [host, setHost] = useState('')
  const [port, setPort] = useState('')
  const [path, setPath] = useState('')
  const [query, setQuery] = useState('')
  const [headers, setHeaders] = useState('')
  const [body, setBody] = useState('')
  const [sending, setSending] = useState(false)
  const [bypassTls, setBypassTls] = useState(false)
  const [selectedReplay, setSelectedReplay] = useState<ReplaySession | null>(null)
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const handleCaptureSelect = (captureId: string) => {
    setSelectedCaptureId(captureId)
    setSelectedReplay(null)
    const capture = captures.find((c) => c.id === captureId)
    if (capture) {
      setMethod(capture.method)
      setHost(capture.host)
      setPort(capture.port ? String(capture.port) : '')
      setPath(capture.path)
      setQuery(capture.query)
      setHeaders(capture.requestHeaders)
      setBody('')
    }
  }

  const handleSend = async () => {
    if (!selectedCaptureId) return
    setSending(true)
    const parsedPort = port ? Number(port) : undefined
    const req: CreateReplayRequest = {
      captureId: selectedCaptureId,
      method: method || undefined,
      host: host || undefined,
      port: Number.isFinite(parsedPort) ? parsedPort : undefined,
      path: path || undefined,
      query: query || undefined,
      requestHeaders: headers || undefined,
      requestBody: body || undefined,
      bypassTlsValidation: bypassTls || undefined,
    }
    const session = await onReplay(req)
    if (session) {
      setSelectedReplay(session)
    }
    setSending(false)
  }

  const formatHeaders = (raw: string): string => {
    try {
      const parsed = JSON.parse(raw)
      return Object.entries(parsed)
        .map(([k, v]) => `${k}: ${v}`)
        .join('\n')
    } catch {
      return raw
    }
  }

  return (
    <div className="manip-section">
      {confirmDeleteId && (
        <ConfirmDialog
          title="Delete replay"
          message="Delete this replay session? This cannot be undone."
          confirmLabel="Delete"
          danger
          onConfirm={() => { onDelete(confirmDeleteId); setConfirmDeleteId(null) }}
          onCancel={() => setConfirmDeleteId(null)}
        />
      )}

      <div className="manip-section__header">
        <span className="manip-section__title">
          Request Replay {loading && <span className="manip-spinner" />}
        </span>
      </div>

      {error && <div className="manip-error">{error}</div>}

      {/* Replay form */}
      <div className="manip-form">
        <div className="manip-form__row">
          <label className="manip-form__label">
            Source Capture
            <select
              className="manip-form__select"
              value={selectedCaptureId}
              onChange={(e) => handleCaptureSelect(e.target.value)}
            >
              <option value="">Select a captured request...</option>
              {captures.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.method} {c.host}{c.path} ({c.statusCode})
                </option>
              ))}
            </select>
          </label>
        </div>

        {selectedCaptureId && (
          <>
            <div className="manip-form__row">
              <label className="manip-form__label">
                Method
                <input
                  className="manip-form__input manip-form__input--narrow"
                  value={method}
                  onChange={(e) => setMethod(e.target.value)}
                />
              </label>
              <label className="manip-form__label">
                Host
                <input
                  className="manip-form__input"
                  value={host}
                  onChange={(e) => setHost(e.target.value)}
                />
              </label>
              <label className="manip-form__label">
                Port
                <input
                  className="manip-form__input manip-form__input--narrow"
                  type="number"
                  value={port}
                  onChange={(e) => setPort(e.target.value)}
                />
              </label>
              <label className="manip-form__label">
                Path
                <input
                  className="manip-form__input"
                  value={path}
                  onChange={(e) => setPath(e.target.value)}
                />
              </label>
              <label className="manip-form__label">
                Query
                <input
                  className="manip-form__input"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                />
              </label>
            </div>
            <div className="manip-form__row manip-form__row--full">
              <label className="manip-form__label manip-form__label--full">
                Request Headers (JSON)
                <textarea
                  className="manip-form__textarea manip-form__textarea--code"
                  value={headers}
                  onChange={(e) => setHeaders(e.target.value)}
                  rows={4}
                  spellCheck={false}
                />
              </label>
            </div>
            <div className="manip-form__row manip-form__row--full">
              <label className="manip-form__label manip-form__label--full">
                Request Body
                <textarea
                  className="manip-form__textarea manip-form__textarea--code"
                  value={body}
                  onChange={(e) => setBody(e.target.value)}
                  rows={4}
                  spellCheck={false}
                />
              </label>
            </div>
            <div className="manip-form__actions">
              <label className="manip-form__label manip-form__label--inline manip-form__label--warning">
                <input
                  type="checkbox"
                  checked={bypassTls}
                  onChange={(e) => setBypassTls(e.target.checked)}
                />
                Bypass TLS validation
                {bypassTls && (
                  <span className="manip-warn-badge" title="TLS validation disabled — use only against trusted test targets">
                    Warning: insecure
                  </span>
                )}
              </label>
              <button
                className="manip-btn manip-btn--primary"
                onClick={handleSend}
                disabled={sending}
              >
                {sending ? 'Sending...' : 'Send Replay'}
              </button>
            </div>
          </>
        )}
      </div>

      {/* Response diff view */}
      {selectedReplay && (
        <div className="replay-response">
          <div className="replay-response__header">
            <span className="replay-response__title">
              Replay Response
            </span>
            <span className={`replay-response__status replay-response__status--${Math.floor(selectedReplay.responseStatusCode / 100)}xx`}>
              {selectedReplay.responseStatusCode}
            </span>
            <span className="replay-response__duration">{selectedReplay.durationMs}ms</span>
          </div>
          <div className="replay-response__section">
            <div className="replay-response__label">Response Headers</div>
            <pre className="replay-response__code">{formatHeaders(selectedReplay.responseHeaders)}</pre>
          </div>
          <div className="replay-response__section">
            <div className="replay-response__label">Response Body</div>
            <pre className="replay-response__code">{selectedReplay.responseBody || '(empty)'}</pre>
          </div>
        </div>
      )}

      {/* Replay history */}
      <div className="manip-table">
        <div className="manip-section__subtitle">Replay History</div>
        {replays.length === 0 ? (
          <div className="manip-empty">
            <div className="manip-empty__message">No replay sessions yet.</div>
            <div className="manip-empty__hint">Select a captured request, optionally edit the method/host/headers, then send it to inspect the response.</div>
          </div>
        ) : (
          replays.map((r) => (
            <div
              key={r.id}
              className={`manip-row manip-row--clickable ${selectedReplay?.id === r.id ? 'manip-row--selected' : ''}`}
              onClick={() => setSelectedReplay(r)}
            >
              <div className="manip-row__main">
                <span className="method-badge-inline">{r.method}</span>
                <span className="manip-row__name">{r.host}{r.path}</span>
                <span className={`replay-status replay-status--${Math.floor(r.responseStatusCode / 100)}xx`}>
                  {r.responseStatusCode}
                </span>
                <span className="manip-row__meta">{r.durationMs}ms</span>
              </div>
              <div className="manip-row__actions">
                <button
                  className="manip-btn manip-btn--small manip-btn--danger"
                  onClick={(e) => { e.stopPropagation(); setConfirmDeleteId(r.id) }}
                >
                  Delete
                </button>
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  )
}
