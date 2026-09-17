import { useEffect, useState, useCallback, useRef } from 'react'
import AppShell from '../components/layout/AppShell'
import Header from '../components/layout/Header'
import SplitPane from '../components/layout/SplitPane'
import CaptureList from '../components/captures/CaptureList'
import CaptureDetail from '../components/captures/CaptureDetail'
import TimelineSwimlaneView from '../components/timeline/TimelineSwimlaneView'
import PanelPacketCapture from '../components/panels/PanelPacketCapture'
import ManipulationPanel from '../components/manipulation/ManipulationPanel'
import SessionsPanel from '../components/sessions/SessionsPanel'
import ScannerPanel from '../components/scanner/ScannerPanel'
import OpenRtbPanel from '../components/openrtb/OpenRtbPanel'
import ProtocolProxyPanel from '../components/proxy/ProtocolProxyPanel'
import InsightTriagePanel from '../components/analytics/InsightTriagePanel'
import PassiveCaptureSummary from '../components/passive/PassiveCaptureSummary'
import DashboardLayoutsMenu from '../components/dashboard/DashboardLayoutsMenu'
import ErrorBoundary from '../components/common/ErrorBoundary'
import DisconnectBanner from '../components/common/DisconnectBanner'
import AlertToastStack from '../components/common/AlertToastStack'
import { useProxy } from '../hooks/useProxy'
import { useCaptures } from '../hooks/useCaptures'
import { useDevices } from '../hooks/useDevices'
import { useTrafficStream } from '../hooks/useTrafficStream'
import { useTheme } from '../hooks/useTheme'
import { useBackendHealth } from '../hooks/useBackendHealth'
import { useKeyboardShortcuts } from '../hooks/useKeyboardShortcuts'
import { useDashboardLayouts } from '../hooks/useDashboardLayouts'
import type { CaptureFilters } from '../types/api'

const VIEW_MODES = ['list', 'timeline', 'packet-capture', 'manipulation', 'sessions', 'scanner', 'openrtb', 'protocol-proxy', 'analytics'] as const
type ViewMode = typeof VIEW_MODES[number]

function isViewMode(value: unknown): value is ViewMode {
  return typeof value === 'string' && (VIEW_MODES as readonly string[]).includes(value)
}

function useIsMobile() {
  const [isMobile, setIsMobile] = useState(() => window.matchMedia('(max-width: 768px)').matches)
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 768px)')
    const handler = (e: MediaQueryListEvent) => setIsMobile(e.matches)
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }, [])
  return isMobile
}

export default function DashboardPage() {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const isMobile = useIsMobile()
  const [filters, setFilters] = useState<CaptureFilters>({ page: 1, pageSize: 50 })
  const [viewMode, setViewMode] = useState<ViewMode>('list')
  const { theme, toggle: toggleTheme } = useTheme()
  const [frozen, setFrozen] = useState(false)
  const [pendingCount, setPendingCount] = useState(0)
  const frozenRef = useRef(frozen)
  frozenRef.current = frozen

  const proxy = useProxy()
  const { devices } = useDevices()
  const {
    captures,
    total,
    loading,
    loadingMore,
    error,
    hasMore,
    loadMore,
    prependCapture,
  } = useCaptures(filters)

  const handleCapture = useCallback((event: Parameters<typeof prependCapture>[0]) => {
    if (frozenRef.current) {
      setPendingCount(c => c + 1)
    } else {
      prependCapture(event)
    }
  }, [prependCapture])

  const handleFreeze = useCallback(() => setFrozen(true), [])
  const handleResume = useCallback(() => {
    setFrozen(false)
    setPendingCount(0)
  }, [])

  const { connectionState } = useTrafficStream({ onCapture: handleCapture })
  const backendStatus = useBackendHealth(connectionState)

  // Escape key: deselect the active capture (closes the detail pane)
  useKeyboardShortcuts({ onEscape: () => { if (selectedId) setSelectedId(null) } })

  // Dashboard layout presets: apply a saved layout/filter JSON pair to local state.
  const applyLayout = useCallback((layoutJson: string, filtersJson: string) => {
    try {
      const layoutState = JSON.parse(layoutJson) as { viewMode?: unknown }
      if (isViewMode(layoutState.viewMode)) {
        setViewMode(layoutState.viewMode)
      }
    } catch {
      // ignore malformed layout JSON
    }
    try {
      const parsedFilters = JSON.parse(filtersJson) as CaptureFilters
      setFilters({ ...parsedFilters, page: 1, pageSize: parsedFilters.pageSize ?? 50 })
    } catch {
      // ignore malformed filters JSON
    }
  }, [])

  // Auto-apply the user's default layout (if any) once, on first load.
  const { layouts: savedLayouts } = useDashboardLayouts()
  const appliedDefaultRef = useRef(false)
  useEffect(() => {
    if (appliedDefaultRef.current || savedLayouts.length === 0) return
    appliedDefaultRef.current = true
    const defaultLayout = savedLayouts.find(l => l.isDefault)
    if (defaultLayout) {
      applyLayout(defaultLayout.layoutJson, defaultLayout.filtersJson)
    }
  }, [savedLayouts, applyLayout])

  const proxyStatus = proxy.status
  const isRunning = proxyStatus?.isRunning ?? false
  const port = proxyStatus?.port ?? proxyStatus?.settings?.proxyPort ?? 8888
  const isPassive = proxyStatus?.settings?.mode === 'Passive'

  return (
    <AppShell
      header={
        <Header
          isRunning={isRunning}
          port={port}
          settings={proxyStatus?.settings ?? null}
          signalRConnected={connectionState === 'connected'}
          loading={proxy.loading}
          theme={theme}
          onStart={proxy.start}
          onStop={proxy.stop}
          onSaveSettings={proxy.saveSettings}
          onToggleTheme={toggleTheme}
        />
      }
    >
      <DisconnectBanner status={backendStatus} />
      <AlertToastStack />

      {/* View mode toggle */}
      <div className="view-toggle">
        {VIEW_MODES.map(mode => (
          <button
            key={mode}
            className={`view-toggle__btn${viewMode === mode ? ' view-toggle__btn--active' : ''}`}
            onClick={() => setViewMode(mode)}
          >
            {mode === 'list' ? 'Requests' :
             mode === 'timeline' ? 'Timeline' :
             mode === 'packet-capture' ? 'Packet Capture' :
             mode === 'manipulation' ? 'Manipulation' :
             mode === 'sessions' ? 'Sessions' :
             mode === 'scanner' ? 'Scanner' :
             mode === 'openrtb' ? 'OpenRTB' :
             mode === 'protocol-proxy' ? 'Protocol Proxies' : 'ML Insights'}
          </button>
        ))}

        <DashboardLayoutsMenu
          currentLayoutJson={JSON.stringify({ viewMode })}
          currentFiltersJson={JSON.stringify(filters)}
          onApply={applyLayout}
        />
      </div>

      {viewMode === 'list' && (
        <div className={`requests-view${isPassive ? ' requests-view--passive' : ''}`}>
          <ErrorBoundary>
            <PassiveCaptureSummary embedded />
          </ErrorBoundary>

          {!isPassive && (
            <div className="requests-view__content">
              <ErrorBoundary>
                {isMobile ? (
                  selectedId ? (
                    <CaptureDetail captureId={selectedId} onBack={() => setSelectedId(null)} />
                  ) : (
                    <CaptureList
                      captures={captures}
                      total={total}
                      loading={loading}
                      loadingMore={loadingMore}
                      error={error}
                      hasMore={hasMore}
                      filters={filters}
                      devices={devices}
                      selectedId={selectedId}
                      onSelect={setSelectedId}
                      onFiltersChange={setFilters}
                      onLoadMore={loadMore}
                      frozen={frozen}
                      pendingCount={pendingCount}
                      onFreeze={handleFreeze}
                      onResume={handleResume}
                    />
                  )
                ) : selectedId ? (
                  <SplitPane
                    left={
                      <CaptureList
                        captures={captures}
                        total={total}
                        loading={loading}
                        loadingMore={loadingMore}
                        error={error}
                        hasMore={hasMore}
                        filters={filters}
                        devices={devices}
                        selectedId={selectedId}
                        onSelect={setSelectedId}
                        onFiltersChange={setFilters}
                        onLoadMore={loadMore}
                        frozen={frozen}
                        pendingCount={pendingCount}
                        onFreeze={handleFreeze}
                        onResume={handleResume}
                      />
                    }
                    right={<CaptureDetail captureId={selectedId} />}
                    initialLeftPercent={40}
                    onCollapse={() => setSelectedId(null)}
                    minLeftPx={260}
                    minRightPx={260}
                  />
                ) : (
                  <CaptureList
                    captures={captures}
                    total={total}
                    loading={loading}
                    loadingMore={loadingMore}
                    error={error}
                    hasMore={hasMore}
                    filters={filters}
                    devices={devices}
                    selectedId={selectedId}
                    onSelect={setSelectedId}
                    onFiltersChange={setFilters}
                    onLoadMore={loadMore}
                    frozen={frozen}
                    pendingCount={pendingCount}
                    onFreeze={handleFreeze}
                    onResume={handleResume}
                  />
                )}
              </ErrorBoundary>
            </div>
          )}
        </div>
      )}

      {viewMode === 'timeline' && (
        <ErrorBoundary>
          <SplitPane
            left={
              <TimelineSwimlaneView
                captures={captures}
                devices={devices}
                selectedId={selectedId}
                onSelect={setSelectedId}
              />
            }
            right={<CaptureDetail captureId={selectedId} />}
            initialLeftPercent={65}
            minLeftPx={400}
            minRightPx={260}
          />
        </ErrorBoundary>
      )}

      {viewMode === 'packet-capture' && (
        <ErrorBoundary>
          <PanelPacketCapture />
        </ErrorBoundary>
      )}

      {viewMode === 'manipulation' && (
        <ErrorBoundary>
          <ManipulationPanel />
        </ErrorBoundary>
      )}

      {viewMode === 'sessions' && (
        <ErrorBoundary>
          <SessionsPanel />
        </ErrorBoundary>
      )}

      {viewMode === 'scanner' && (
        <ErrorBoundary>
          <ScannerPanel />
        </ErrorBoundary>
      )}

      {viewMode === 'openrtb' && (
        <ErrorBoundary>
          <OpenRtbPanel />
        </ErrorBoundary>
      )}

      {viewMode === 'protocol-proxy' && (
        <ErrorBoundary>
          <ProtocolProxyPanel />
        </ErrorBoundary>
      )}

      {viewMode === 'analytics' && (
        <ErrorBoundary>
          <InsightTriagePanel
            onOpenCapture={id => { setSelectedId(id); setViewMode('list') }}
          />
        </ErrorBoundary>
      )}

    </AppShell>
  )
}
