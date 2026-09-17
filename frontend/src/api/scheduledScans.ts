import { apiFetch } from './client'

export interface ScheduledScan {
  id: string
  deviceId?: string | null
  targetCidr?: string | null
  targetTag?: string | null
  cronExpression: string
  isEnabled: boolean
  lastRunAt?: string
  nextRunAt?: string
  lastScanJobId?: string
  lastRunStatus?: string | null
  lastRunError?: string | null
  createdAt: string
}

export interface CreateScheduledScanRequest {
  deviceId?: string
  targetCidr?: string
  targetTag?: string
  cronExpression?: string
}

export interface UpdateScheduledScanRequest {
  isEnabled?: boolean
  cronExpression?: string
  deviceId?: string
  targetCidr?: string
  targetTag?: string
}

export function listScheduledScans(): Promise<ScheduledScan[]> {
  return apiFetch<ScheduledScan[]>('/api/scheduled-scans')
}

export function getScheduledScan(id: string): Promise<ScheduledScan> {
  return apiFetch<ScheduledScan>(`/api/scheduled-scans/${id}`)
}

export function createScheduledScan(req: CreateScheduledScanRequest): Promise<ScheduledScan> {
  return apiFetch<ScheduledScan>('/api/scheduled-scans', {
    method: 'POST',
    body: JSON.stringify(req),
  })
}

export function updateScheduledScan(id: string, req: UpdateScheduledScanRequest): Promise<ScheduledScan> {
  return apiFetch<ScheduledScan>(`/api/scheduled-scans/${id}`, {
    method: 'PUT',
    body: JSON.stringify(req),
  })
}

export function deleteScheduledScan(id: string): Promise<void> {
  return apiFetch<void>(`/api/scheduled-scans/${id}`, { method: 'DELETE' })
}
