import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import AlertToastStack from '../components/common/AlertToastStack'

const mockDismiss = vi.fn()
let mockAlerts: { id: string; title: string; body: string; severity: string; timestamp: string }[] = []

vi.mock('../hooks/useAlertNotifications', () => ({
  useAlertNotifications: () => ({ alerts: mockAlerts, dismiss: mockDismiss }),
}))

describe('AlertToastStack', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockAlerts = []
  })

  it('renders nothing when there are no alerts', () => {
    const { container } = render(<AlertToastStack />)
    expect(container).toBeEmptyDOMElement()
  })

  it('renders a toast for each alert with title and body', () => {
    mockAlerts = [
      { id: 'a1', title: 'Rule fired', body: 'Host: example.com, Path: /api', severity: 'Warning', timestamp: '2026-05-14T00:00:00Z' },
      { id: 'a2', title: 'Breakpoint fired', body: 'Host: other.com, Path: /x', severity: 'Critical', timestamp: '2026-05-14T00:01:00Z' },
    ]
    render(<AlertToastStack />)

    expect(screen.getByText('Rule fired')).toBeInTheDocument()
    expect(screen.getByText('Host: example.com, Path: /api')).toBeInTheDocument()
    expect(screen.getByText('Breakpoint fired')).toBeInTheDocument()
    expect(screen.getAllByRole('alert')).toHaveLength(2)
  })

  it('applies a severity-specific class', () => {
    mockAlerts = [{ id: 'a1', title: 'T', body: 'B', severity: 'Critical', timestamp: '2026-05-14T00:00:00Z' }]
    render(<AlertToastStack />)

    expect(screen.getByRole('alert')).toHaveClass('alert-toast--critical')
  })

  it('calls dismiss when the close button is clicked', async () => {
    mockAlerts = [{ id: 'a1', title: 'T', body: 'B', severity: 'Info', timestamp: '2026-05-14T00:00:00Z' }]
    render(<AlertToastStack />)

    await userEvent.click(screen.getByRole('button', { name: /dismiss notification/i }))
    expect(mockDismiss).toHaveBeenCalledWith('a1')
  })
})
