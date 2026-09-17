import { useState, useEffect } from 'react'
import {
  listScheduledScans,
  createScheduledScan,
  updateScheduledScan,
  deleteScheduledScan,
  type ScheduledScan,
} from '../../api/scheduledScans'

interface Props {
  deviceId?: string
}

type TargetMode = 'device' | 'cidr' | 'tag'

export function ScheduledScansPanel({ deviceId }: Props) {
  const [scans, setScans] = useState<ScheduledScan[]>([])
  const [loading, setLoading] = useState(false)
  const [cronExpr, setCronExpr] = useState('0 * * * *')
  const [targetMode, setTargetMode] = useState<TargetMode>('device')
  const [targetCidr, setTargetCidr] = useState('')
  const [targetTag, setTargetTag] = useState('')
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    try {
      const all = await listScheduledScans()
      setScans(deviceId ? all.filter((s) => s.deviceId === deviceId) : all)
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [deviceId])

  const handleCreate = async () => {
    try {
      if (targetMode === 'device') {
        if (!deviceId) return
        await createScheduledScan({ deviceId, cronExpression: cronExpr })
      } else if (targetMode === 'cidr') {
        if (!targetCidr.trim()) return
        await createScheduledScan({ targetCidr: targetCidr.trim(), cronExpression: cronExpr })
      } else {
        if (!targetTag.trim()) return
        await createScheduledScan({ targetTag: targetTag.trim(), cronExpression: cronExpr })
      }
      setTargetCidr('')
      setTargetTag('')
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const handleToggle = async (scan: ScheduledScan) => {
    try {
      await updateScheduledScan(scan.id, { isEnabled: !scan.isEnabled })
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const handleDelete = async (id: string) => {
    try {
      await deleteScheduledScan(id)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="p-4">
      <h3 className="text-lg font-semibold mb-4">Scheduled Scans</h3>

      {error && (
        <div className="bg-red-100 text-red-800 p-2 rounded mb-3 text-sm">{error}</div>
      )}

      <div className="mb-4 border rounded p-3">
        <div className="flex gap-4 mb-2 text-sm">
          <label className="flex items-center gap-1">
            <input
              type="radio"
              name="targetMode"
              checked={targetMode === 'device'}
              onChange={() => setTargetMode('device')}
              disabled={!deviceId}
            />
            Device{!deviceId && ' (select a device first)'}
          </label>
          <label className="flex items-center gap-1">
            <input
              type="radio"
              name="targetMode"
              checked={targetMode === 'cidr'}
              onChange={() => setTargetMode('cidr')}
            />
            CIDR
          </label>
          <label className="flex items-center gap-1">
            <input
              type="radio"
              name="targetMode"
              checked={targetMode === 'tag'}
              onChange={() => setTargetMode('tag')}
            />
            Tag
          </label>
        </div>

        <div className="flex gap-2">
          {targetMode === 'cidr' && (
            <input
              className="border rounded px-2 py-1 text-sm flex-1"
              value={targetCidr}
              onChange={(e) => setTargetCidr(e.target.value)}
              placeholder="CIDR (e.g. 10.0.0.0/24)"
            />
          )}
          {targetMode === 'tag' && (
            <input
              className="border rounded px-2 py-1 text-sm flex-1"
              value={targetTag}
              onChange={(e) => setTargetTag(e.target.value)}
              placeholder="Tag (e.g. camera)"
            />
          )}
          <input
            className="border rounded px-2 py-1 text-sm flex-1"
            value={cronExpr}
            onChange={(e) => setCronExpr(e.target.value)}
            placeholder="Cron expression (e.g. 0 * * * *)"
          />
          <button
            className="bg-blue-600 text-white px-3 py-1 rounded text-sm"
            onClick={handleCreate}
            disabled={targetMode === 'device' && !deviceId}
          >
            Add Schedule
          </button>
        </div>
      </div>

      {loading ? (
        <p className="text-sm text-gray-500">Loading...</p>
      ) : scans.length === 0 ? (
        <p className="text-sm text-gray-500">No scheduled scans configured.</p>
      ) : (
        <table className="w-full text-sm border-collapse">
          <thead>
            <tr className="bg-gray-100">
              <th className="border px-2 py-1 text-left">Target</th>
              <th className="border px-2 py-1 text-left">Cron</th>
              <th className="border px-2 py-1 text-left">Enabled</th>
              <th className="border px-2 py-1 text-left">Last Run</th>
              <th className="border px-2 py-1 text-left">Next Run</th>
              <th className="border px-2 py-1 text-left">Actions</th>
            </tr>
          </thead>
          <tbody>
            {scans.map((scan) => (
              <tr key={scan.id}>
                <td className="border px-2 py-1 font-mono text-xs">
                  {scan.targetCidr
                    ? `CIDR: ${scan.targetCidr}`
                    : scan.targetTag
                      ? `Tag: ${scan.targetTag}`
                      : scan.deviceId
                        ? `Device: ${scan.deviceId}`
                        : '—'}
                </td>
                <td className="border px-2 py-1 font-mono">{scan.cronExpression}</td>
                <td className="border px-2 py-1">
                  <input
                    type="checkbox"
                    checked={scan.isEnabled}
                    onChange={() => handleToggle(scan)}
                  />
                </td>
                <td className="border px-2 py-1">
                  {scan.lastRunAt ? new Date(scan.lastRunAt).toLocaleString() : '—'}
                </td>
                <td className="border px-2 py-1">
                  {scan.nextRunAt ? new Date(scan.nextRunAt).toLocaleString() : '—'}
                </td>
                <td className="border px-2 py-1">
                  <button
                    className="text-red-600 hover:underline text-xs"
                    onClick={() => handleDelete(scan.id)}
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
  )
}
