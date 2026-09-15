import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import LoginPage from '../pages/LoginPage'
import { ApiError, NetworkError } from '../api/client'
import type { AuthState } from '../store/authStore'

const mockLogin = vi.fn()

vi.mock('../hooks/useAuth', () => ({
  useLogin: () => mockLogin,
}))

vi.mock('../store/authStore', () => ({
  useAuthState: vi.fn(),
}))

import { useAuthState } from '../store/authStore'

const singleUserState: AuthState = { status: 'unauthenticated', token: null, multiUser: false, backendUnavailable: false }
const multiUserState: AuthState = { status: 'unauthenticated', token: null, multiUser: true, backendUnavailable: false }
const backendDownState: AuthState = { status: 'unauthenticated', token: null, multiUser: false, backendUnavailable: true }

describe('LoginPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(useAuthState).mockReturnValue(singleUserState)
  })

  it('shows only password field when multiUser is false', () => {
    render(<LoginPage />)
    expect(screen.queryByLabelText('Username')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
  })

  it('shows username and password fields when multiUser is true', () => {
    vi.mocked(useAuthState).mockReturnValue(multiUserState)
    render(<LoginPage />)
    expect(screen.getByLabelText('Username')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
  })

  it('submits with hardcoded admin username when multiUser is false', async () => {
    mockLogin.mockResolvedValueOnce(undefined)
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(mockLogin).toHaveBeenCalledWith({ username: 'admin', password: 'secret' })
  })

  it('submits with the typed username when multiUser is true', async () => {
    vi.mocked(useAuthState).mockReturnValue(multiUserState)
    mockLogin.mockResolvedValueOnce(undefined)
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Username'), 'alice')
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(mockLogin).toHaveBeenCalledWith({ username: 'alice', password: 'secret' })
  })

  it('shows error message on failed login', async () => {
    mockLogin.mockRejectedValueOnce(new ApiError(401, 'Invalid credentials'))
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Password'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await waitFor(() => expect(screen.getByText('Invalid credentials')).toBeInTheDocument())
  })

  it('disables the submit button while loading', async () => {
    mockLogin.mockImplementation(() => new Promise(() => {})) // never resolves
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(screen.getByRole('button', { name: 'Signing in…' })).toBeDisabled()
  })

  it('shows a disconnect banner when the backend is unavailable', () => {
    vi.mocked(useAuthState).mockReturnValue(backendDownState)
    render(<LoginPage />)
    expect(screen.getByRole('alert')).toHaveTextContent(/backend is unavailable/i)
  })

  it('shows a network-specific message when login fails to reach the server', async () => {
    mockLogin.mockRejectedValueOnce(new NetworkError())
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await waitFor(() =>
      expect(screen.getByText('Cannot reach the server. Please check your connection and try again.')).toBeInTheDocument(),
    )
  })

  it('shows a generic server-error message on a 5xx login response', async () => {
    mockLogin.mockRejectedValueOnce(new ApiError(500, 'Internal Server Error'))
    render(<LoginPage />)
    await userEvent.type(screen.getByLabelText('Password'), 'secret')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await waitFor(() =>
      expect(screen.getByText('Server error — please try again shortly.')).toBeInTheDocument(),
    )
  })
})
