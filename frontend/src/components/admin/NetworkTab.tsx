import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { configureFirewall, FIREWALL_SCRIPT_URL, type FirewallRuleResult } from '../../api/firewall'
import { getToken } from '../../api/client'

export default function NetworkTab() {
  const [toast, setToast] = useState<string | null>(null)
  const [results, setResults] = useState<FirewallRuleResult[] | null>(null)
  const [downloading, setDownloading] = useState(false)

  const showToast = (msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(null), 4000)
  }

  const configureMutation = useMutation({
    mutationFn: configureFirewall,
    onSuccess: (data) => {
      setResults(data.results)
      showToast(data.overallSuccess
        ? 'Firewall rules configured successfully'
        : 'Some firewall rules could not be applied — see results below')
    },
    onError: (err) => {
      showToast(err instanceof Error ? err.message : 'Failed to configure firewall')
    },
  })

  async function downloadScript() {
    setDownloading(true)
    try {
      const token = getToken()
      const resp = await fetch(FIREWALL_SCRIPT_URL, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
      if (!resp.ok) {
        showToast('Failed to generate firewall script')
        return
      }
      const blob = await resp.blob()
      const objUrl = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = objUrl
      a.download = 'iotspy-configure-firewall.ps1'
      a.click()
      URL.revokeObjectURL(objUrl)
    } finally {
      setDownloading(false)
    }
  }

  return (
    <>
      {toast && <div className="admin-toast">{toast}</div>}

      <div className="admin-section">
        <div className="admin-section-title">Automatic Firewall Configuration</div>
        <div className="admin-card" style={{ maxWidth: 640 }}>
          <div style={{
            fontSize: 'var(--font-size-xs)', color: 'var(--color-text-muted)',
            background: 'rgba(230, 160, 0, 0.1)', border: '1px solid var(--color-warning, #e6a000)',
            borderRadius: 'var(--radius-sm)', padding: 'var(--space-2) var(--space-3)', marginBottom: 'var(--space-3)',
          }}>
            This attempts to add inbound firewall rules directly (Windows only). It requires the IoTSpy
            server process to be running elevated (as Administrator) — results may vary depending on
            privileges, Windows edition, and any third-party firewall software. If it fails or only
            partially succeeds, use the downloadable script below instead.
          </div>
          <div className="admin-card__actions">
            <button
              className="admin-btn admin-btn--primary"
              disabled={configureMutation.isPending}
              onClick={() => configureMutation.mutate()}
            >
              {configureMutation.isPending ? 'Configuring…' : 'Configure Firewall'}
            </button>
          </div>

          {results && (
            <div style={{ marginTop: 'var(--space-3)' }}>
              <table className="admin-table">
                <thead>
                  <tr>
                    <th>Rule</th>
                    <th>Port</th>
                    <th>Result</th>
                  </tr>
                </thead>
                <tbody>
                  {results.map(r => (
                    <tr key={`${r.protocol}-${r.port}`}>
                      <td>{r.label}</td>
                      <td>{r.protocol}/{r.port}</td>
                      <td style={{ color: r.success ? 'var(--color-success, #2e8b57)' : 'var(--color-error)' }}>
                        {r.success ? '✓ OK' : `✗ ${r.message}`}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      <div className="admin-section">
        <div className="admin-section-title">Manual Setup Script</div>
        <div className="admin-card" style={{ maxWidth: 640 }}>
          <div className="admin-card__stats">
            Download a PowerShell script with the <code>netsh advfirewall</code> commands for IoTSpy's
            current ports. Run it yourself from an elevated PowerShell prompt — recommended for advanced
            users or when automatic configuration doesn't fully succeed.
          </div>
          <div className="admin-card__actions">
            <button className="admin-btn" disabled={downloading} onClick={downloadScript}>
              {downloading ? 'Preparing…' : 'Download Setup Script (Windows)'}
            </button>
          </div>
        </div>
      </div>
    </>
  )
}
