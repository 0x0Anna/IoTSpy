import { useEffect, useRef, useState } from 'react'
import type { ProxyMode, ProxySettings, ProxySettingsUpdate } from '../../types/api'
import OnboardingWizard from '../onboarding/OnboardingWizard'
import '../../styles/modal.css'

interface Props {
  settings: ProxySettings
  onSave: (update: ProxySettingsUpdate) => Promise<ProxySettings | null>
  onClose: () => void
}

export default function SettingsModal({ settings, onSave, onClose }: Props) {
  const [proxyPort, setProxyPort] = useState(String(settings.proxyPort))
  const [listenAddress, setListenAddress] = useState(settings.listenAddress)
  const [mode, setMode] = useState<ProxyMode>(settings.mode)
  const [captureTls, setCaptureTls] = useState(settings.captureTls)
  const [captureRequestBodies, setCaptureRequestBodies] = useState(settings.captureRequestBodies)
  const [captureResponseBodies, setCaptureResponseBodies] = useState(settings.captureResponseBodies)
  const [maxBodySizeKb, setMaxBodySizeKb] = useState(String(settings.maxBodySizeKb))
  const [autoStart, setAutoStart] = useState(settings.autoStart)
  const [transparentProxyPort, setTransparentProxyPort] = useState(String(settings.transparentProxyPort))
  const [targetDeviceIp, setTargetDeviceIp] = useState(settings.targetDeviceIp)
  const [gatewayIp, setGatewayIp] = useState(settings.gatewayIp)
  const [networkInterface, setNetworkInterface] = useState(settings.networkInterface)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [showWizard, setShowWizard] = useState(false)

  // Close on Escape
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // Trap focus
  const modalRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    modalRef.current?.focus()
  }, [])

  async function handleSave() {
    const port = Number(proxyPort)
    const bodyKb = Number(maxBodySizeKb)
    if (!port || port < 1 || port > 65535) {
      setError('Proxy port must be between 1 and 65535.')
      return
    }
    if (!bodyKb || bodyKb < 1) {
      setError('Max body size must be at least 1 KB.')
      return
    }
    if (mode === 'ArpSpoof' && (!targetDeviceIp || !gatewayIp || !networkInterface)) {
      setError('ARP Spoof mode requires Target Device IP, Gateway IP, and Network Interface.')
      return
    }
    setSaving(true)
    setError(null)
    const update: ProxySettingsUpdate = {
      proxyPort: port,
      listenAddress,
      mode,
      captureTls,
      captureRequestBodies,
      captureResponseBodies,
      maxBodySizeKb: bodyKb,
      autoStart,
    }
    if (mode === 'GatewayRedirect') {
      update.transparentProxyPort = Number(transparentProxyPort) || settings.transparentProxyPort
    }
    if (mode === 'ArpSpoof') {
      update.transparentProxyPort = Number(transparentProxyPort) || settings.transparentProxyPort
      update.targetDeviceIp = targetDeviceIp
      update.gatewayIp = gatewayIp
      update.networkInterface = networkInterface
    }
    const result = await onSave(update)
    setSaving(false)
    if (result) {
      onClose()
    } else {
      setError('Failed to save settings. Check the console for details.')
    }
  }

  if (showWizard) {
    return <OnboardingWizard onComplete={() => { setShowWizard(false) }} />
  }

  return (
    <div className="modal-overlay" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="settings-modal-title"
        ref={modalRef}
        tabIndex={-1}
      >
        <div className="modal__header">
          <h2 className="modal__title" id="settings-modal-title">Proxy Settings</h2>
          <button className="modal__close" onClick={onClose} aria-label="Close">✕</button>
        </div>

        <div className="modal__body">
          {error && <div className="modal-error">{error}</div>}

          <div className="settings-section-title">Network</div>

          <div className="settings-row">
            <div className="settings-field">
              <label className="settings-label" htmlFor="proxy-port">Proxy Port</label>
              <input
                id="proxy-port"
                className="settings-input"
                type="number"
                min={1}
                max={65535}
                value={proxyPort}
                onChange={(e) => setProxyPort(e.target.value)}
              />
            </div>
            <div className="settings-field">
              <label className="settings-label" htmlFor="listen-addr">Listen Address</label>
              <input
                id="listen-addr"
                className="settings-input"
                type="text"
                value={listenAddress}
                onChange={(e) => setListenAddress(e.target.value)}
                placeholder="0.0.0.0"
              />
            </div>
          </div>

          <div className="settings-section-title">Mode</div>

          <div className="settings-field">
            <label className="settings-label" htmlFor="proxy-mode">Proxy Mode</label>
            <select
              id="proxy-mode"
              className="settings-select"
              value={mode}
              onChange={(e) => setMode(e.target.value as ProxyMode)}
            >
              <option value="ExplicitProxy">Explicit Proxy</option>
              <option value="GatewayRedirect">Gateway Redirect</option>
              <option value="ArpSpoof">ARP Spoof</option>
              <option value="Passive">Passive (observe-only)</option>
            </select>
            {mode === 'Passive' && (
              <div className="settings-hint">
                Passive mode can also be controlled from the <strong>Passive Capture</strong> panel in the dashboard — that panel includes start/stop controls and device-filter options.
              </div>
            )}
          </div>

          {(mode === 'GatewayRedirect' || mode === 'ArpSpoof') && (
            <div className="settings-field" style={{ maxWidth: 180 }}>
              <label className="settings-label" htmlFor="transparent-port">Transparent Proxy Port</label>
              <input
                id="transparent-port"
                className="settings-input"
                type="number"
                min={1}
                max={65535}
                value={transparentProxyPort}
                onChange={(e) => setTransparentProxyPort(e.target.value)}
              />
            </div>
          )}

          {mode === 'ArpSpoof' && (
            <div className="settings-row">
              <div className="settings-field">
                <label className="settings-label" htmlFor="target-ip">Target Device IP</label>
                <input
                  id="target-ip"
                  className="settings-input"
                  type="text"
                  value={targetDeviceIp}
                  onChange={(e) => setTargetDeviceIp(e.target.value)}
                  placeholder="192.168.1.100"
                />
              </div>
              <div className="settings-field">
                <label className="settings-label" htmlFor="gateway-ip">Gateway IP</label>
                <input
                  id="gateway-ip"
                  className="settings-input"
                  type="text"
                  value={gatewayIp}
                  onChange={(e) => setGatewayIp(e.target.value)}
                  placeholder="192.168.1.1"
                />
              </div>
            </div>
          )}

          {mode === 'ArpSpoof' && (
            <div className="settings-field">
              <label className="settings-label" htmlFor="net-iface">Network Interface</label>
              <input
                id="net-iface"
                className="settings-input"
                type="text"
                value={networkInterface}
                onChange={(e) => setNetworkInterface(e.target.value)}
                placeholder="eth0"
              />
            </div>
          )}

          <div className="settings-section-title">Capture</div>

          <label className="settings-checkbox-row">
            <input
              type="checkbox"
              checked={captureTls}
              onChange={(e) => setCaptureTls(e.target.checked)}
            />
            Intercept TLS (HTTPS MITM)
          </label>

          <label className="settings-checkbox-row">
            <input
              type="checkbox"
              checked={captureRequestBodies}
              onChange={(e) => setCaptureRequestBodies(e.target.checked)}
            />
            Capture request bodies
          </label>

          <label className="settings-checkbox-row">
            <input
              type="checkbox"
              checked={captureResponseBodies}
              onChange={(e) => setCaptureResponseBodies(e.target.checked)}
            />
            Capture response bodies
          </label>

          <div className="settings-field" style={{ maxWidth: 180 }}>
            <label className="settings-label" htmlFor="max-body">Max body size (KB)</label>
            <input
              id="max-body"
              className="settings-input"
              type="number"
              min={1}
              value={maxBodySizeKb}
              onChange={(e) => setMaxBodySizeKb(e.target.value)}
            />
          </div>

          <div className="settings-section-title">Startup</div>

          <label className="settings-checkbox-row">
            <input
              type="checkbox"
              checked={autoStart}
              onChange={(e) => setAutoStart(e.target.checked)}
            />
            Auto-start proxy when server launches
          </label>

          <div className="settings-section-title">Help</div>

          <button
            type="button"
            className="btn-modal-cancel"
            style={{ width: '100%' }}
            onClick={() => setShowWizard(true)}
          >
            Relaunch Welcome Guide
          </button>
        </div>

        <div className="modal__footer">
          <button className="btn-modal-cancel" onClick={onClose} disabled={saving}>
            Cancel
          </button>
          <button className="btn-modal-save" onClick={handleSave} disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  )
}
