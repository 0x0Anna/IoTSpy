import { useState } from 'react'
import { Navigate } from 'react-router-dom'
import AppShell from '../components/layout/AppShell'
import Header from '../components/layout/Header'
import DatabaseTab from '../components/admin/DatabaseTab'
import CertificatesTab from '../components/admin/CertificatesTab'
import NetworkTab from '../components/admin/NetworkTab'
import AuditLogTab from '../components/admin/AuditLogTab'
import UsersTab from '../components/admin/UsersTab'
import ApiKeysTab from '../components/admin/ApiKeysTab'
import PluginsTab from '../components/admin/PluginsTab'
import ScanScopesTab from '../components/admin/ScanScopesTab'
import { useCurrentUser } from '../hooks/useAuth'
import { useProxy } from '../hooks/useProxy'
import { useTheme } from '../hooks/useTheme'
import '../styles/admin.css'

type AdminTab = 'database' | 'certificates' | 'network' | 'audit' | 'users' | 'apikeys' | 'plugins' | 'scopes'

const TABS: { key: AdminTab; label: string }[] = [
  { key: 'database', label: 'Database' },
  { key: 'certificates', label: 'Certificates' },
  { key: 'network', label: 'Network' },
  { key: 'audit', label: 'Audit Log' },
  { key: 'users', label: 'Users' },
  { key: 'apikeys', label: 'API Keys' },
  { key: 'plugins', label: 'Plugins' },
  { key: 'scopes', label: 'Scan Scopes' },
]

export default function AdminPage() {
  const currentUser = useCurrentUser()
  const [activeTab, setActiveTab] = useState<AdminTab>('database')
  const proxy = useProxy()
  const { theme, toggle: toggleTheme } = useTheme()

  if (!currentUser || currentUser.role !== 'admin') {
    return <Navigate to="/" replace />
  }

  const isRunning = proxy.status?.isRunning ?? false
  const port = proxy.status?.port ?? 8888

  return (
    <AppShell
      header={
        <Header
          isRunning={isRunning}
          port={port}
          settings={proxy.status?.settings ?? null}
          signalRConnected={false}
          loading={proxy.loading}
          theme={theme}
          onStart={proxy.start}
          onStop={proxy.stop}
          onSaveSettings={proxy.saveSettings}
          onToggleTheme={toggleTheme}
        />
      }
    >
      <div className="admin-page">
        <div className="admin-header">
          <h1>System Administration</h1>
        </div>

        <div className="admin-tabs">
          {TABS.map(tab => (
            <button
              key={tab.key}
              className={`admin-tab${activeTab === tab.key ? ' admin-tab--active' : ''}`}
              onClick={() => setActiveTab(tab.key)}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div className="admin-content">
          {activeTab === 'database' && <DatabaseTab />}
          {activeTab === 'certificates' && <CertificatesTab />}
          {activeTab === 'network' && <NetworkTab />}
          {activeTab === 'audit' && <AuditLogTab />}
          {activeTab === 'users' && <UsersTab currentUsername={currentUser.username} />}
          {activeTab === 'apikeys' && <ApiKeysTab />}
          {activeTab === 'plugins' && <PluginsTab />}
          {activeTab === 'scopes' && <ScanScopesTab />}
        </div>
      </div>
    </AppShell>
  )
}
