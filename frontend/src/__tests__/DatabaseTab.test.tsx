import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import DatabaseTab from '../components/admin/DatabaseTab'

const mockApiFetch = vi.fn()

vi.mock('../api/client', () => ({
  apiFetch: (...args: unknown[]) => mockApiFetch(...args),
  getToken: () => 'test-token',
}))

const initialRetention = {
  enabled: true,
  captureRetentionDays: 30,
  packetRetentionDays: 30,
  scanJobRetentionDays: 90,
  openRtbEventRetentionDays: 14,
  protocolMessageRetentionDays: 14,
  hostBaselineRetentionDays: 30,
  auditRetentionDays: 90,
  auditArchivePurgeDays: 365,
  runIntervalHours: 24,
}

const mockStats = {
  captures: { count: 0, estimatedSizeBytes: 0, oldestTimestamp: null },
  packets: { count: 0, estimatedSizeBytes: 0, oldestTimestamp: null },
  scanFindings: { count: 0 },
  auditLog: { count: 0, archiveCount: 0, oldestTimestamp: null, oldestArchiveTimestamp: null },
}

function renderWithClient() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <DatabaseTab />
    </QueryClientProvider>,
  )
}

function sliderFor(label: string): HTMLElement {
  const labelEl = screen.getByText(new RegExp(`^${label}:`))
  const container = labelEl.closest('div') as HTMLElement
  return within(container).getByRole('slider')
}

describe('DatabaseTab retention settings', () => {
  beforeEach(() => {
    mockApiFetch.mockReset()
    mockApiFetch.mockImplementation((url: string, opts?: RequestInit) => {
      if (url === '/api/admin/stats') return Promise.resolve(mockStats)
      if (url === '/api/admin/retention' && (!opts || opts.method === undefined)) {
        return Promise.resolve(initialRetention)
      }
      if (url === '/api/admin/retention' && opts?.method === 'PUT') {
        return Promise.resolve({ message: 'saved' })
      }
      return Promise.resolve({})
    })
  })

  it('does not revert a slider value after a successful save', async () => {
    renderWithClient()

    await screen.findByText(/Anomaly Baselines TTL: 30d/)

    const anomalySlider = sliderFor('Anomaly Baselines TTL')
    fireEvent.change(anomalySlider, { target: { value: '60' } })
    expect(await screen.findByText(/Anomaly Baselines TTL: 60d/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    // Regression: the save mutation's onSuccess must seed the query cache with the
    // saved settings, not just clear the dirty flag — otherwise the sync effect that
    // applies fetched retention data re-fires with the still-stale cached value and
    // snaps the slider back to 30d until a manual reload refetches the real value.
    await waitFor(() => {
      expect(screen.getByText(/Anomaly Baselines TTL: 60d/)).toBeInTheDocument()
    })

    // Give any pending re-renders a chance to run, then assert it's still correct —
    // this is the part that would have failed before the fix (a delayed revert).
    await new Promise(resolve => setTimeout(resolve, 50))
    expect(screen.getByText(/Anomaly Baselines TTL: 60d/)).toBeInTheDocument()
  })
})
