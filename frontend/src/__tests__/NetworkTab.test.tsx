import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import NetworkTab from '../components/admin/NetworkTab'

const mockConfigureFirewall = vi.fn()

vi.mock('../api/firewall', () => ({
  configureFirewall: (...args: unknown[]) => mockConfigureFirewall(...args),
  FIREWALL_SCRIPT_URL: '/api/admin/firewall/script?platform=windows',
}))

function renderWithClient(ui: React.ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>)
}

describe('NetworkTab', () => {
  beforeEach(() => {
    mockConfigureFirewall.mockReset()
  })

  it('renders the elevation warning and download script action', () => {
    renderWithClient(<NetworkTab />)
    expect(screen.getByText(/requires the IoTSpy server process to be running elevated/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Download Setup Script/i })).toBeInTheDocument()
  })

  it('configures the firewall and renders per-rule results', async () => {
    mockConfigureFirewall.mockResolvedValue({
      overallSuccess: false,
      results: [
        { port: 5000, protocol: 'TCP', label: 'API (HTTP)', success: true, message: 'OK' },
        { port: 5001, protocol: 'TCP', label: 'API (HTTPS)', success: false, message: 'Access denied' },
      ],
    })

    renderWithClient(<NetworkTab />)
    await userEvent.click(screen.getByRole('button', { name: /Configure Firewall/i }))

    expect(await screen.findByText('✓ OK')).toBeInTheDocument()
    expect(screen.getByText('✗ Access denied')).toBeInTheDocument()
    expect(screen.getByText(/Some firewall rules could not be applied/i)).toBeInTheDocument()
  })

  it('shows a success toast when all rules apply', async () => {
    mockConfigureFirewall.mockResolvedValue({
      overallSuccess: true,
      results: [{ port: 5000, protocol: 'TCP', label: 'API (HTTP)', success: true, message: 'OK' }],
    })

    renderWithClient(<NetworkTab />)
    await userEvent.click(screen.getByRole('button', { name: /Configure Firewall/i }))

    expect(await screen.findByText('Firewall rules configured successfully')).toBeInTheDocument()
  })
})
