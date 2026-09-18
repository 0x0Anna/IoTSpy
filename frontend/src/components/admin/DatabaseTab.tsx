import { useState, useEffect } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import type { AdminStats } from '../../types/api'
import { apiFetch, getToken } from '../../api/client'

interface RetentionSettings {
  enabled: boolean
  captureRetentionDays: number
  packetRetentionDays: number
  scanJobRetentionDays: number
  openRtbEventRetentionDays: number
  protocolMessageRetentionDays: number
  hostBaselineRetentionDays: number
  auditRetentionDays: number
  auditArchivePurgeDays: number
  runIntervalHours: number
}

interface ConfirmState {
  title: string
  message: string
  onConfirm: () => Promise<unknown>
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString()
}

const STATS_KEY = ['admin-stats']

export default function DatabaseTab() {
  const queryClient = useQueryClient()
  const [confirm, setConfirm] = useState<ConfirmState | null>(null)
  const [toast, setToast] = useState<string | null>(null)

  const [captureDays, setCaptureDays] = useState(30)
  const [captureHost, setCaptureHost] = useState('')
  const [packetDays, setPacketDays] = useState(30)
  const [auditArchiveDays, setAuditArchiveDays] = useState(90)
  const [auditPurgeDays, setAuditPurgeDays] = useState(365)

  const [retention, setRetention] = useState<RetentionSettings | null>(null)
  const [retentionDirty, setRetentionDirty] = useState(false)

  const {
    data: stats,
    isLoading: loading,
    error: queryError,
  } = useQuery<AdminStats>({
    queryKey: STATS_KEY,
    queryFn: () => apiFetch<AdminStats>('/api/admin/stats'),
  })

  const { data: retentionData } = useQuery<RetentionSettings>({
    queryKey: ['admin-retention'],
    queryFn: () => apiFetch<RetentionSettings>('/api/admin/retention'),
  })

  useEffect(() => {
    if (retentionData && !retentionDirty) setRetention(retentionData)
  }, [retentionData, retentionDirty])

  const retentionMutation = useMutation({
    mutationFn: (settings: RetentionSettings) =>
      apiFetch<{ message: string }>('/api/admin/retention', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settings),
      }),
    onSuccess: () => {
      showToast('Retention settings saved')
      setRetentionDirty(false)
    },
    onError: () => showToast('Failed to save retention settings'),
  })

  const purgeMutation = useMutation({
    mutationFn: (url: string) =>
      apiFetch<{ deleted: number }>(url, { method: 'DELETE' }),
    onSuccess: (result, url) => {
      const type = url.includes('captures') ? 'captures' : url.includes('packets') ? 'packets' : 'records'
      showToast(`Deleted ${result.deleted} ${type}`)
      void queryClient.invalidateQueries({ queryKey: STATS_KEY })
    },
    onError: () => showToast('Purge failed'),
  })

  const archiveAuditMutation = useMutation({
    mutationFn: (days: number) =>
      apiFetch<{ archived: number }>(`/api/admin/audit/archive?olderThanDays=${days}`, { method: 'POST' }),
    onSuccess: (result) => {
      showToast(`Archived ${result.archived} audit entries`)
      void queryClient.invalidateQueries({ queryKey: STATS_KEY })
    },
    onError: () => showToast('Archive failed'),
  })

  const purgeAuditArchiveMutation = useMutation({
    mutationFn: (days: number) =>
      apiFetch<{ purged: number }>(`/api/admin/audit/archive?olderThanDays=${days}`, { method: 'DELETE' }),
    onSuccess: (result) => {
      showToast(`Purged ${result.purged} archived entries`)
      void queryClient.invalidateQueries({ queryKey: STATS_KEY })
    },
    onError: () => showToast('Purge failed'),
  })

  const busy = purgeMutation.isPending || archiveAuditMutation.isPending || purgeAuditArchiveMutation.isPending || retentionMutation.isPending

  const showToast = (msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(null), 3000)
  }

  const runWithConfirm = (title: string, message: string, action: () => Promise<unknown>) => {
    setConfirm({ title, message, onConfirm: action })
  }

  const purgeCaptures = (params: string) =>
    purgeMutation.mutateAsync(`/api/admin/captures?${params}`)

  const purgePackets = (params: string) =>
    purgeMutation.mutateAsync(`/api/admin/packets?${params}`)

  const downloadExport = async (url: string, filename: string) => {
    const token = getToken()
    const resp = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
    if (!resp.ok) {
      showToast('Export failed')
      return
    }
    const blob = await resp.blob()
    const objUrl = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = objUrl
    a.download = filename
    a.click()
    URL.revokeObjectURL(objUrl)
  }

  if (loading) return <p style={{ color: 'var(--color-text-muted)' }}>Loading stats…</p>
  if (queryError) return <p style={{ color: 'var(--color-error)' }}>Failed to load stats</p>
  if (!stats) return null

  return (
    <>
      {toast && <div className="admin-toast">{toast}</div>}

      <div className="admin-cards">
        {retention && (
          <div className="admin-card" style={{ gridColumn: '1 / -1' }}>
            <div className="admin-card__title">Automatic Retention</div>
            <div className="admin-card__stats" style={{ marginBottom: 'var(--space-3)' }}>
              Background service purges old records on a schedule.
              {' '}<span style={{ color: 'var(--color-text-muted)', fontSize: 'var(--font-size-xs)' }}>
                Changes take effect on the next pass; update appsettings.json for persistence across restarts.
              </span>
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', marginBottom: 'var(--space-4)' }}>
              <label style={{ fontSize: 'var(--font-size-sm)', fontWeight: 600 }}>
                Enable automatic retention
              </label>
              <button
                role="switch"
                aria-checked={retention.enabled}
                className={`admin-btn ${retention.enabled ? 'admin-btn--primary' : ''}`}
                style={{ minWidth: 80 }}
                onClick={() => { setRetention(r => r ? { ...r, enabled: !r.enabled } : r); setRetentionDirty(true) }}
              >
                {retention.enabled ? 'Enabled' : 'Disabled'}
              </button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 'var(--space-4)', marginBottom: 'var(--space-4)' }}>
              {([
                ['Captures TTL', 'captureRetentionDays', 1, 730] as const,
                ['Packets TTL', 'packetRetentionDays', 1, 365] as const,
                ['Scan Jobs TTL', 'scanJobRetentionDays', 1, 730] as const,
                ['OpenRTB Events TTL', 'openRtbEventRetentionDays', 1, 365] as const,
                ['Protocol Messages TTL', 'protocolMessageRetentionDays', 1, 365] as const,
                ['Anomaly Baselines TTL', 'hostBaselineRetentionDays', 1, 365] as const,
                ['Audit Archive TTL', 'auditRetentionDays', 0, 730] as const,
                ['Audit Purge TTL', 'auditArchivePurgeDays', 0, 3650] as const,
              ] as const).map(([label, field, min, max]) => (
                <div key={field}>
                  <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
                    {label}: {retention[field] === 0 ? 'never' : `${retention[field]}d`}
                  </label>
                  <input
                    type="range"
                    min={min}
                    max={max}
                    value={retention[field]}
                    style={{ width: '100%' }}
                    onChange={e => {
                      const val = +e.target.value
                      setRetention(r => r ? { ...r, [field]: val } : r)
                      setRetentionDirty(true)
                    }}
                  />
                </div>
              ))}
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', marginBottom: 'var(--space-4)' }}>
              <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)' }}>
                Run every {retention.runIntervalHours}h
              </label>
              <input
                type="range"
                min={1}
                max={168}
                step={1}
                value={retention.runIntervalHours}
                style={{ flex: 1 }}
                onChange={e => {
                  setRetention(r => r ? { ...r, runIntervalHours: +e.target.value } : r)
                  setRetentionDirty(true)
                }}
              />
            </div>

            <div className="admin-card__actions">
              <button
                className="admin-btn admin-btn--primary"
                disabled={busy || !retentionDirty}
                onClick={() => retentionMutation.mutate(retention)}
              >
                Save
              </button>
              {retentionDirty && (
                <button className="admin-btn" onClick={() => { setRetention(retentionData ?? null); setRetentionDirty(false) }}>
                  Discard
                </button>
              )}
            </div>
          </div>
        )}

        <div className="admin-card">
          <div className="admin-card__title">Captures &amp; Logs</div>
          <div className="admin-card__stats">
            {stats.captures.count.toLocaleString()} rows
            &nbsp;·&nbsp; ~{formatBytes(stats.captures.estimatedSizeBytes)}
            &nbsp;·&nbsp; oldest: {formatDate(stats.captures.oldestTimestamp)}
          </div>
          <div style={{ marginBottom: 'var(--space-3)' }}>
            <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
              Purge older than {captureDays} days
            </label>
            <input type="range" min={1} max={365} value={captureDays}
              onChange={e => setCaptureDays(+e.target.value)}
              style={{ width: '100%', marginBottom: 4 }} />
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Purge old captures',
                `Delete all captures older than ${captureDays} days?`,
                () => purgeCaptures(`olderThanDays=${captureDays}`),
              )}>Purge by age</button>
          </div>
          <div style={{ marginBottom: 'var(--space-3)' }}>
            <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
              Purge by host
            </label>
            <div style={{ display: 'flex', gap: 'var(--space-1)' }}>
              <input placeholder="e.g. api.example.com" value={captureHost}
                onChange={e => setCaptureHost(e.target.value)}
                style={{ flex: 1, fontSize: 'var(--font-size-xs)', background: 'var(--color-surface)', border: '1px solid var(--color-border)', borderRadius: 'var(--radius-sm)', color: 'var(--color-text)', padding: '4px 8px' }} />
              <button className="admin-btn admin-btn--danger" disabled={busy || !captureHost.trim()} onClick={() =>
                runWithConfirm(
                  'Purge captures by host',
                  `Delete all captures for host "${captureHost}"?`,
                  () => purgeCaptures(`host=${encodeURIComponent(captureHost)}`),
                )}>Purge</button>
            </div>
          </div>
          <div className="admin-card__actions">
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Purge all captures',
                `Delete ALL ${stats.captures.count.toLocaleString()} captures? This cannot be undone.`,
                () => purgeCaptures('purgeAll=true'),
              )}>Purge all</button>
            <button className="admin-btn" onClick={() => downloadExport('/api/admin/export/logs?format=json', 'captures.json')}>Export JSON</button>
            <button className="admin-btn" onClick={() => downloadExport('/api/admin/export/logs?format=csv', 'captures.csv')}>Export CSV</button>
          </div>
        </div>

        <div className="admin-card">
          <div className="admin-card__title">Packets</div>
          <div className="admin-card__stats">
            {stats.packets.count.toLocaleString()} rows
            &nbsp;·&nbsp; ~{formatBytes(stats.packets.estimatedSizeBytes)}
            &nbsp;·&nbsp; oldest: {formatDate(stats.packets.oldestTimestamp)}
          </div>
          <div style={{ marginBottom: 'var(--space-3)' }}>
            <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
              Purge older than {packetDays} days
            </label>
            <input type="range" min={1} max={365} value={packetDays}
              onChange={e => setPacketDays(+e.target.value)}
              style={{ width: '100%', marginBottom: 4 }} />
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Purge old packets',
                `Delete all packets older than ${packetDays} days?`,
                () => purgePackets(`olderThanDays=${packetDays}`),
              )}>Purge by age</button>
          </div>
          <div className="admin-card__actions">
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Purge all packets',
                `Delete ALL ${stats.packets.count.toLocaleString()} packets?`,
                () => purgePackets('purgeAll=true'),
              )}>Purge all</button>
            <button className="admin-btn" onClick={() => downloadExport('/api/admin/export/packets?format=json', 'packets.json')}>Export JSON</button>
            <button className="admin-btn" onClick={() => downloadExport('/api/admin/export/packets?format=csv', 'packets.csv')}>Export CSV</button>
          </div>
        </div>

        <div className="admin-card">
          <div className="admin-card__title">Audit Log</div>
          <div className="admin-card__stats">
            {stats.auditLog.count.toLocaleString()} active entries
            &nbsp;·&nbsp; oldest: {formatDate(stats.auditLog.oldestTimestamp)}
          </div>
          <div className="admin-card__stats" style={{ marginBottom: 'var(--space-3)', color: 'var(--color-text-muted)' }}>
            Archive: {stats.auditLog.archiveCount.toLocaleString()} entries
            &nbsp;·&nbsp; oldest: {formatDate(stats.auditLog.oldestArchiveTimestamp)}
          </div>
          <div style={{ marginBottom: 'var(--space-3)' }}>
            <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
              Archive active entries older than {auditArchiveDays} days
            </label>
            <input type="range" min={7} max={730} value={auditArchiveDays}
              onChange={e => setAuditArchiveDays(+e.target.value)}
              style={{ width: '100%', marginBottom: 4 }} />
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Archive audit entries',
                `Move audit entries older than ${auditArchiveDays} days to the archive? This cannot be undone.`,
                () => archiveAuditMutation.mutateAsync(auditArchiveDays),
              )}>Archive by age</button>
          </div>
          <div style={{ marginBottom: 'var(--space-3)' }}>
            <label style={{ fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)', display: 'block', marginBottom: 4 }}>
              Purge archive entries older than {auditPurgeDays} days
            </label>
            <input type="range" min={30} max={3650} value={auditPurgeDays}
              onChange={e => setAuditPurgeDays(+e.target.value)}
              style={{ width: '100%', marginBottom: 4 }} />
            <button className="admin-btn admin-btn--danger" disabled={busy} onClick={() =>
              runWithConfirm(
                'Purge audit archive',
                `Permanently delete archive entries older than ${auditPurgeDays} days? This cannot be undone.`,
                () => purgeAuditArchiveMutation.mutateAsync(auditPurgeDays),
              )}>Purge archive</button>
          </div>
        </div>

        <div className="admin-card">
          <div className="admin-card__title">Configuration</div>
          <div className="admin-card__stats">
            Rules, breakpoints, scheduled scans, OpenRTB policies, API specs
          </div>
          <div className="admin-card__actions">
            <button className="admin-btn admin-btn--primary"
              onClick={() => downloadExport('/api/admin/export/config', 'iotspy-config.json')}>
              Export JSON
            </button>
          </div>
        </div>
      </div>

      {confirm && (
        <div className="admin-overlay">
          <div className="admin-dialog">
            <h3>{confirm.title}</h3>
            <p>{confirm.message}</p>
            <div className="admin-dialog__actions">
              <button className="admin-btn" onClick={() => setConfirm(null)}>Cancel</button>
              <button className="admin-btn admin-btn--danger" disabled={busy} onClick={async () => {
                await confirm.onConfirm()
                setConfirm(null)
              }}>Confirm</button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
