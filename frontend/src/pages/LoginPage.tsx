import { useState } from 'react'
import { ApiError, NetworkError } from '../api/client'
import { useLogin } from '../hooks/useAuth'
import { useAuthState } from '../store/authStore'
import DisconnectBanner from '../components/common/DisconnectBanner'
import '../styles/auth.css'

export default function LoginPage() {
  const login = useLogin()
  const { multiUser, backendUnavailable } = useAuthState()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      await login({ username: multiUser ? username : 'admin', password })
    } catch (err) {
      if (err instanceof NetworkError) {
        setError('Cannot reach the server. Please check your connection and try again.')
      } else if (err instanceof ApiError) {
        setError(err.status >= 500 ? 'Server error — please try again shortly.' : err.message)
      } else {
        setError('Login failed.')
      }
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="auth-page">
      <div className="auth-card">
        <div className="auth-logo">
          <div className="auth-logo-icon">I</div>
          <span className="auth-title">IoTSpy</span>
        </div>
        <p className="auth-subtitle">Sign in to your dashboard.</p>
        {backendUnavailable && <DisconnectBanner status="down" />}
        <form className="auth-form" onSubmit={handleSubmit}>
          {error && <div className="auth-error">{error}</div>}
          {multiUser && (
            <div className="form-group">
              <label className="form-label" htmlFor="username">Username</label>
              <input
                id="username"
                className="form-input"
                type="text"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoFocus
                required
              />
            </div>
          )}
          <div className="form-group">
            <label className="form-label" htmlFor="password">Password</label>
            <input
              id="password"
              className="form-input"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoFocus={!multiUser}
              required
            />
          </div>
          <button className="btn-primary" type="submit" disabled={loading}>
            {loading ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  )
}
