import { getToken } from './client'

const BASE_URL = import.meta.env.VITE_API_URL ?? ''

type ReportScope = 'devices' | 'sessions'
type ReportFormat = 'html' | 'pdf'

function reportUrl(scope: ReportScope, id: string, format: ReportFormat): string {
  return `${BASE_URL}/api/reports/${scope}/${id}/${format}`
}

async function downloadReport(scope: ReportScope, id: string, format: ReportFormat, filenamePrefix: string): Promise<void> {
  const token = getToken()
  const res = await fetch(reportUrl(scope, id, format), {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  })
  if (!res.ok) throw new Error(`Failed to download ${format.toUpperCase()} report: ${res.status}`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `${filenamePrefix}-${id}.${format}`
  a.click()
  URL.revokeObjectURL(url)
}

export async function downloadHtmlReport(deviceId: string): Promise<void> {
  await downloadReport('devices', deviceId, 'html', 'scan-report')
}

export async function downloadPdfReport(deviceId: string): Promise<void> {
  await downloadReport('devices', deviceId, 'pdf', 'scan-report')
}

export function getHtmlReportUrl(deviceId: string): string {
  return reportUrl('devices', deviceId, 'html')
}

export function getPdfReportUrl(deviceId: string): string {
  return reportUrl('devices', deviceId, 'pdf')
}

export async function downloadSessionHtmlReport(sessionId: string): Promise<void> {
  await downloadReport('sessions', sessionId, 'html', 'session-report')
}

export async function downloadSessionPdfReport(sessionId: string): Promise<void> {
  await downloadReport('sessions', sessionId, 'pdf', 'session-report')
}

export function getSessionHtmlReportUrl(sessionId: string): string {
  return reportUrl('sessions', sessionId, 'html')
}

export function getSessionPdfReportUrl(sessionId: string): string {
  return reportUrl('sessions', sessionId, 'pdf')
}
