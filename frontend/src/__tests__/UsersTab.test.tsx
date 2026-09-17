import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import UsersTab from '../components/admin/UsersTab'
import type { UserSummary } from '../types/api'

const mockApiFetch = vi.fn()

vi.mock('../api/client', () => ({
  apiFetch: (...args: unknown[]) => mockApiFetch(...args),
  ApiError: class ApiError extends Error {},
}))

const mockUsers: UserSummary[] = [
  { id: 'u1', username: 'admin', displayName: 'Admin', role: 'admin', isEnabled: true, createdAt: '2026-05-14T00:00:00Z', lastLoginAt: null },
  { id: 'u2', username: 'bob', displayName: 'Bob', role: 'viewer', isEnabled: true, createdAt: '2026-05-13T00:00:00Z', lastLoginAt: null },
]

function renderWithClient(ui: React.ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>)
}

describe('UsersTab', () => {
  beforeEach(() => {
    mockApiFetch.mockReset()
    mockApiFetch.mockResolvedValue(mockUsers)
  })

  it('renders the user list', async () => {
    renderWithClient(<UsersTab currentUsername="admin" />)
    expect(await screen.findByText(/admin \(you\)/)).toBeInTheDocument()
    expect(screen.getByText('bob')).toBeInTheDocument()
  })

  it('submits the create form and calls POST /api/auth/users', async () => {
    renderWithClient(<UsersTab currentUsername="admin" />)
    await screen.findByText('bob')

    await userEvent.click(screen.getByRole('button', { name: /Add user/i }))
    // Create-dialog inputs have no htmlFor/id label association, so scope the query
    // to the dialog and select by role/type instead.
    const dialog = screen.getByText('Create User').closest('.admin-dialog') as HTMLElement
    const [usernameInput] = within(dialog).getAllByRole('textbox')
    const passwordInput = dialog.querySelector('input[type="password"]')!
    await userEvent.type(usernameInput, 'newuser')
    await userEvent.type(passwordInput, 'hunter2')
    await userEvent.click(within(dialog).getByRole('button', { name: /^Create$/i }))

    expect(mockApiFetch).toHaveBeenCalledWith(
      '/api/auth/users',
      expect.objectContaining({ method: 'POST' })
    )
  })

  it('updates role via the select and calls PUT /api/auth/users/{id}', async () => {
    renderWithClient(<UsersTab currentUsername="admin" />)
    await screen.findByText('bob')

    const roleSelects = screen.getAllByRole('combobox')
    await userEvent.selectOptions(roleSelects[1], 'operator')

    expect(mockApiFetch).toHaveBeenCalledWith(
      '/api/auth/users/u2',
      expect.objectContaining({ method: 'PUT', body: JSON.stringify({ role: 'Operator' }) })
    )
  })

  it('shows a confirmation dialog before deleting, then calls DELETE', async () => {
    renderWithClient(<UsersTab currentUsername="admin" />)
    await screen.findByText('bob')

    await userEvent.click(screen.getByRole('button', { name: /Delete/i }))
    expect(screen.getByText(/Delete account/i)).toBeInTheDocument()

    await userEvent.click(screen.getAllByRole('button', { name: /^Delete$/i })[1])

    expect(mockApiFetch).toHaveBeenCalledWith('/api/auth/users/u2', expect.objectContaining({ method: 'DELETE' }))
  })

  it('does not show a delete button for the current user', async () => {
    renderWithClient(<UsersTab currentUsername="admin" />)
    await screen.findByText('bob')

    // Only one delete button should exist (for "bob"), not for "admin" (currentUsername)
    expect(screen.getAllByRole('button', { name: /^Delete$/i })).toHaveLength(1)
  })
})
