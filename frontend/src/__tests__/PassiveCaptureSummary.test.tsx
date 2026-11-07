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

function statusWith(mode: ProxyStatus['settings']['mode'], isPassive = false): ProxyStatus {
  return {
    isRunning: true,
    port: 8888,
    settings: {
      id: 1,
      proxyPort: 8888,
      mode,
      isPassive,
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
      tlsPassthroughHosts: '',
      autoPassthroughOnTlsFailure: false,
    },
  }
}

describe('PassiveCaptureSummary — passive toggle', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockSaveSettings.mockResolvedValue(null)
  })

  it('turns passive on without touching the transport mode', async () => {
    mockStatus = statusWith('GatewayRedirect')
    render(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Passive' }))
    expect(mockSaveSettings).toHaveBeenCalledWith({ isPassive: true })
  })

  it('turns passive off without touching the transport mode', async () => {
    mockStatus = statusWith('GatewayRedirect', true)
    render(<PassiveCaptureSummary />)

    await userEvent.click(screen.getByRole('button', { name: 'Active' }))
    expect(mockSaveSettings).toHaveBeenCalledWith({ isPassive: false })
  })

  it('shows the passive state from the flag, whatever the mode', () => {
    mockStatus = statusWith('ArpSpoof', true)
    render(<PassiveCaptureSummary />)

    expect(screen.getByText('Passive — observing, not recording')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Passive' })).toBeDisabled()
  })
})
