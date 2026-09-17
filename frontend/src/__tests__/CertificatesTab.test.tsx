import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import CertificatesTab from '../components/admin/CertificatesTab'

const mockApiFetch = vi.fn()
const mockSaveSettings = vi.fn()

vi.mock('../api/client', () => ({
  apiFetch: (...args: unknown[]) => mockApiFetch(...args),
  ApiError: class ApiError extends Error {},
}))

const mockProxyStatus = {
  settings: {
    caCommonName: 'Acme Corp CA',
    caOrganization: 'Acme Corp',
    caCountry: 'US',
    caValidityYears: 10,
  },
}

vi.mock('../hooks/useProxy', () => ({
  useProxy: () => ({
    status: mockProxyStatus,
    saveSettings: mockSaveSettings,
  }),
}))

const mockRootCa = {
  id: 'ca-1',
  commonName: 'Acme Corp CA',
  serialNumber: 'ABC123',
  notBefore: '2026-01-01T00:00:00Z',
  notAfter: '2036-01-01T00:00:00Z',
  certificatePem: '-----BEGIN CERTIFICATE-----\nMOCK\n-----END CERTIFICATE-----',
}

function renderWithClient(ui: React.ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>)
}

describe('CertificatesTab', () => {
  beforeEach(() => {
    mockApiFetch.mockReset()
    mockSaveSettings.mockReset()
    mockApiFetch.mockImplementation((url: string) => {
      if (url === '/api/certificates/root-ca') return Promise.resolve(mockRootCa)
      if (url === '/api/certificates') return Promise.resolve([])
      return Promise.resolve({})
    })
  })

  it('renders CA naming fields seeded from current proxy settings', async () => {
    renderWithClient(<CertificatesTab />)
    expect(await screen.findByDisplayValue('Acme Corp CA')).toBeInTheDocument()
    expect(screen.getByDisplayValue('Acme Corp')).toBeInTheDocument()
    expect(screen.getByDisplayValue('US')).toBeInTheDocument()
    expect(screen.getByDisplayValue('10')).toBeInTheDocument()
  })

  it('saves edited CA naming fields via saveSettings', async () => {
    mockSaveSettings.mockResolvedValue({ caCommonName: 'New Name' })
    renderWithClient(<CertificatesTab />)

    const commonNameInput = await screen.findByDisplayValue('Acme Corp CA')
    await userEvent.clear(commonNameInput)
    await userEvent.type(commonNameInput, 'New Name')
    await userEvent.click(screen.getByRole('button', { name: /Save Naming/i }))

    expect(mockSaveSettings).toHaveBeenCalledWith(
      expect.objectContaining({ caCommonName: 'New Name' }),
    )
    expect(await screen.findByText('CA naming settings saved')).toBeInTheDocument()
  })

  it('shows an error toast when saving CA naming fails', async () => {
    mockSaveSettings.mockResolvedValue(null)
    renderWithClient(<CertificatesTab />)

    await screen.findByDisplayValue('Acme Corp CA')
    await userEvent.click(screen.getByRole('button', { name: /Save Naming/i }))

    expect(await screen.findByText('Failed to save CA naming settings')).toBeInTheDocument()
  })
})
