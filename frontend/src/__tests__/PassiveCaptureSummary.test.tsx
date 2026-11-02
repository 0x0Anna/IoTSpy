import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import PassiveCaptureSummary from '../components/passive/PassiveCaptureSummary'
import type { ProxyStatus } from '../types/api'

const mockSaveSettings = vi.fn()

// useProxy's status is swapped out per test via this mutable holder, mimicking the real
// hook's data changing after a save/refetch (its tests mock the hook the same way).
let mockStatus: ProxyStatus | null = null

vi.mock('../hooks/useProxy', () => ({
  useProxy: () => ({
    status: mockStatus,
    loading: false,
    error: null,
    refresh: vi.fn(),
    start: vi.fn(),
    stop: vi.fn(),
    saveSettings: mockSaveSettings,
  }),
}))

vi.mock('../api/passiveCapture', () => ({
  getPassiveSummary: vi.fn().mockResolvedValue({
    totalRequests: 0,
    topEndpoints: [],
    statusCodes: [],
    topHosts: [],
    activeDeviceFilter: [],
  }),
  listPassiveSessions: vi.fn().mockResolvedValue([]),
  clearBuffer: vi.fn(),
  clearDeviceFilter: vi.fn(),
  deletePassiveSession: vi.fn(),
  savePassiveSession: vi.fn(),
  setDeviceFilter: vi.fn(),
}))

function statusWith(mode: ProxyStatus['settings']['mode']): ProxyStatus {
  return {
    isRunning: true,
    port: 8888,
    settings: {
      id: 1,
      proxyPort: 8888,
      mode,
      isRunning: true,
      captureTls: true,
      captureRequestBodies: true,
      captureResponseBodies: true,
      maxBodySizeKb: 1024,
      listenAddress: '0.0.0.0',
      transparentProxyPort: 9999,
      targetDeviceIp: '',
      gatewayIp: '',
      networkInterface: 'eth1',
      caCommonName: 'IoTSpy CA',
      caOrganization: 'IoTSpy',
      caCountry: 'US',
      caValidityYears: 10,
      autoStart: true,
      passwordHash: '',
    },
  }
}

describe('PassiveCaptureSummary — mode restore', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockSaveSettings.mockResolvedValue(null)
  })

  it('restores GatewayRedirect (not ExplicitProxy) when leaving Passive mode', async () => {
    mockStatus = statusWith('GatewayRedirect')
    const { rerender } = render(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Passive' }))
    expect(mockSaveSettings).toHaveBeenCalledWith({ mode: 'Passive' })

    // The server is now in Passive mode; the hook's data reflects that after the save.
    mockStatus = statusWith('Passive')
    rerender(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Active' }))
    expect(mockSaveSettings).toHaveBeenLastCalledWith({ mode: 'GatewayRedirect' })
  })

  it('restores ArpSpoof (not ExplicitProxy) when leaving Passive mode', async () => {
    mockStatus = statusWith('ArpSpoof')
    const { rerender } = render(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Passive' }))
    mockStatus = statusWith('Passive')
    rerender(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Active' }))
    expect(mockSaveSettings).toHaveBeenLastCalledWith({ mode: 'ArpSpoof' })
  })

  it('falls back to ExplicitProxy when no prior mode was ever observed', async () => {
    mockStatus = statusWith('Passive')
    render(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Active' }))
    expect(mockSaveSettings).toHaveBeenCalledWith({ mode: 'ExplicitProxy' })
  })
})
