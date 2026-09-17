import { useEffect, useState, useCallback } from 'react'
import * as signalR from '@microsoft/signalr'
import { getToken } from '../api/client'

export type AlertSeverity = 'Info' | 'Warning' | 'Critical'

export interface AlertNotification {
  id: string
  title: string
  body: string
  severity: AlertSeverity
  timestamp: string
}

const AUTO_DISMISS_MS = 8000

export function useAlertNotifications() {
  const [alerts, setAlerts] = useState<AlertNotification[]>([])

  const dismiss = useCallback((id: string) => {
    setAlerts(prev => prev.filter(a => a.id !== id))
  }, [])

  useEffect(() => {
    const token = getToken()
    if (!token) return

    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/collaboration', { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .build()

    conn.on('Alert', (payload: Omit<AlertNotification, 'id'>) => {
      const alert: AlertNotification = { ...payload, id: crypto.randomUUID() }
      setAlerts(prev => [alert, ...prev].slice(0, 20))
      setTimeout(() => dismiss(alert.id), AUTO_DISMISS_MS)
    })

    conn.start().catch(() => { /* SignalR unavailable */ })

    return () => {
      void conn.stop()
    }
  }, [dismiss])

  return { alerts, dismiss }
}
