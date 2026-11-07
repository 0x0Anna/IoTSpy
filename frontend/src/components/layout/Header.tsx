import { useState, useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import ProxyStatusBadge from '../proxy/ProxyStatusBadge'
import SettingsModal from '../proxy/SettingsModal'
import { useCurrentUser, useLogout } from '../../hooks/useAuth'
import type { ProxySettings, ProxySettingsUpdate } from '../../types/api'
import type { Theme } from '../../hooks/useTheme'
import '../../styles/header.css'

interface Props {
  isRunning: boolean
  port: number
  settings: ProxySettings | null
  signalRConnected: boolean
  loading: boolean
  theme: Theme
  onStart: () => void
  onStop: () => void
  onSaveSettings: (update: ProxySettingsUpdate) => Promise<ProxySettings | null>
  onToggleTheme: () => void
}

export default function Header({
  isRunning,
  port,
  settings,
  signalRConnected,
  loading,
  theme,
  onStart,
  onStop,
  onSaveSettings,
  onToggleTheme,
}: Props) {
  const logout = useLogout()
  const currentUser = useCurrentUser()
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const menuWrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!menuOpen) return
    function handleOutsideClick(e: MouseEvent) {
      if (menuWrapRef.current && !menuWrapRef.current.contains(e.target as Node)) {
        setMenuOpen(false)
      }
    }
    document.addEventListener('mousedown', handleOutsideClick)
    return () => document.removeEventListener('mousedown', handleOutsideClick)
  }, [menuOpen])

  return (
    <>
      <header className="header">
        <a className="header__logo" href="/">
          <div className="header__logo-icon">
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
              <circle cx="8" cy="11" r="2" fill="currentColor"/>
              <path d="M5 8.5A4.24 4.24 0 0 1 8 7.5a4.24 4.24 0 0 1 3 1" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
              <path d="M2 5.5A8.5 8.5 0 0 1 8 4a8.5 8.5 0 0 1 6 1.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" opacity="0.6"/>
            </svg>
          </div>
          <span className="header__logo-text">IoTSpy</span>
        </a>

        <ProxyStatusBadge
          isRunning={isRunning}
          port={port}
          signalRConnected={signalRConnected}
        />
        {isRunning && settings?.isPassive && (
          <span className="header__passive-badge" title="Proxy is in passive observe-only mode — no manipulation or DB writes">
            Passive Mode
          </span>
        )}

        <div className="header__spacer" />

        <div className="header__actions">
          {isRunning ? (
            <button
              className="header__btn header__btn--stop"
              onClick={onStop}
              disabled={loading}
              title="Stop proxy"
            >
              Stop
            </button>
          ) : (
            <button
              className="header__btn header__btn--start"
              onClick={onStart}
              disabled={loading}
              title="Start proxy"
            >
              Start
            </button>
          )}

          <a
            className="header__btn"
            href="/api/certificates/root-ca/download"
            download
            title="Download root CA certificate"
          >
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
              <path d="M7 1v8M4 6l3 3 3-3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
              <path d="M2 11h10" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
            </svg>
            <span className="header__btn-label">Root CA</span>
          </a>

          {currentUser?.role === 'admin' && (
            <Link
              className="header__btn header__btn--icon header__btn--desktop"
              to="/admin"
              title="System administration"
              aria-label="Admin"
            >
              &#x1F527;
            </Link>
          )}

          <button
            className="header__btn header__btn--icon header__btn--desktop"
            onClick={onToggleTheme}
            title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
            aria-label="Toggle theme"
          >
            {theme === 'dark' ? '☀' : '☾'}
          </button>

          <button
            className="header__btn header__btn--icon header__btn--desktop"
            onClick={() => setSettingsOpen(true)}
            title="Proxy settings"
            aria-label="Proxy settings"
          >
            &#x2699;
          </button>

          <button
            className="header__btn header__btn--icon header__btn--desktop"
            onClick={logout}
            title="Sign out"
            aria-label="Sign out"
          >
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
              <path d="M6 2H3a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
              <path d="M10.5 10.5 13 8l-2.5-2.5M13 8H6" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
          </button>

          {/* Overflow menu — visible only on mobile (≤640px) */}
          <div className="header__menu-wrap" ref={menuWrapRef}>
            <button
              className="header__btn header__btn--icon header__hamburger"
              onClick={() => setMenuOpen(o => !o)}
              aria-label="More options"
              title="More options"
            >
              <svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">
                <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
              </svg>
            </button>

            {menuOpen && (
              <div className="header__menu" role="menu">
                {currentUser?.role === 'admin' && (
                  <Link
                    className="header__menu-item"
                    to="/admin"
                    role="menuitem"
                    onClick={() => setMenuOpen(false)}
                  >
                    <span aria-hidden="true">&#x1F527;</span> Admin
                  </Link>
                )}
                <button
                  className="header__menu-item"
                  role="menuitem"
                  onClick={() => { onToggleTheme(); setMenuOpen(false) }}
                >
                  <span aria-hidden="true">{theme === 'dark' ? '☀' : '☾'}</span>
                  {theme === 'dark' ? 'Light mode' : 'Dark mode'}
                </button>
                <button
                  className="header__menu-item"
                  role="menuitem"
                  onClick={() => { setSettingsOpen(true); setMenuOpen(false) }}
                >
                  <span aria-hidden="true">&#x2699;</span> Settings
                </button>
                <button
                  className="header__menu-item header__menu-item--danger"
                  role="menuitem"
                  onClick={() => { logout(); setMenuOpen(false) }}
                >
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">
                    <path d="M6 2H3a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
                    <path d="M10.5 10.5 13 8l-2.5-2.5M13 8H6" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
                  </svg>
                  Sign out
                </button>
              </div>
            )}
          </div>
        </div>
      </header>

      {settingsOpen && settings && (
        <SettingsModal
          settings={settings}
          onSave={onSaveSettings}
          onClose={() => setSettingsOpen(false)}
        />
      )}
    </>
  )
}
