import { apiFetch } from './client'

export interface FirewallRuleResult {
  port: number
  protocol: string
  label: string
  success: boolean
  message: string
}

export interface ConfigureFirewallResponse {
  results: FirewallRuleResult[]
  overallSuccess: boolean
}

export function configureFirewall(): Promise<ConfigureFirewallResponse> {
  return apiFetch<ConfigureFirewallResponse>('/api/admin/firewall/configure', {
    method: 'POST',
    body: JSON.stringify({ platform: 'windows' }),
  })
}

export const FIREWALL_SCRIPT_URL = '/api/admin/firewall/script?platform=windows'
