import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import SettingsModal from '../components/proxy/SettingsModal'
import type { ProxySettings } from '../types/api'

const settings: ProxySettings = {
  id: 1, proxyPort: 8888, mode: 'GatewayRedirect', isRunning: true, captureTls: true,
  captureRequestBodies: true, captureResponseBodies: true, maxBodySizeKb: 1024, listenAddress: '0.0.0.0',
  passwordHash: '', autoStart: true, transparentProxyPort: 9999, targetDeviceIp: '', gatewayIp: '',
  networkInterface: '', caCommonName: 'IoTSpy CA', caOrganization: 'IoTSpy', caCountry: 'US', caValidityYears: 10,
  tlsPassthroughHosts: '*.apple.com', autoPassthroughOnTlsFailure: false,
}

describe('SettingsModal TLS passthrough', () => {
  it('loads and saves the passthrough list and auto-passthrough toggle', async () => {
    const user = userEvent.setup()
    const onSave = vi.fn().mockResolvedValue(settings)
    render(<SettingsModal settings={settings} onSave={onSave} onClose={vi.fn()} />)

    const list = screen.getByLabelText(/TLS passthrough hosts/)
    expect(list).toHaveValue('*.apple.com')
    await user.type(list, '\ngateway.icloud.com')
    await user.click(screen.getByLabelText(/Auto-passthrough hosts/))
    await user.click(screen.getByRole('button', { name: /save/i }))

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      tlsPassthroughHosts: '*.apple.com\ngateway.icloud.com',
      autoPassthroughOnTlsFailure: true,
    }))
  })

  it('hides the passthrough options when TLS interception is off', () => {
    render(<SettingsModal settings={{ ...settings, captureTls: false }} onSave={vi.fn()} onClose={vi.fn()} />)

    expect(screen.queryByLabelText(/TLS passthrough hosts/)).not.toBeInTheDocument()
  })
})
