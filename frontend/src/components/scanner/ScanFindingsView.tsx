import { useState } from 'react'
import type { ScanFinding, ScanFindingSeverity, ScanFindingType } from '../../types/api'
import '../../styles/scanner.css'

interface Props {
  findings: ScanFinding[]
}

const UNCATEGORIZED = 'Uncategorized'

type GroupBy = 'severity' | 'type' | 'cve' | 'service'

const groupByOptions: { value: GroupBy; label: string }[] = [
  { value: 'severity', label: 'Severity' },
  { value: 'type', label: 'Type' },
  { value: 'cve', label: 'CVE' },
  { value: 'service', label: 'Service' },
]

const severityOrder: Record<ScanFindingSeverity, number> = {
  Critical: 0,
  High: 1,
  Medium: 2,
  Low: 3,
  Info: 4,
}

const severityClass: Record<ScanFindingSeverity, string> = {
  Critical: 'severity--critical',
  High: 'severity--high',
  Medium: 'severity--medium',
  Low: 'severity--low',
  Info: 'severity--info',
}

const typeOrder: ScanFindingType[] = [
  'OpenPort',
  'ServiceBanner',
  'DefaultCredential',
  'Cve',
  'ConfigIssue',
]

const typeLabel: Record<ScanFindingType, string> = {
  OpenPort: 'Open Port',
  ServiceBanner: 'Service Banner',
  DefaultCredential: 'Default Credential',
  Cve: 'CVE',
  ConfigIssue: 'Config Issue',
}

interface GroupResult {
  keys: string[]
  groups: Record<string, ScanFinding[]>
  headerLabel: (key: string) => string
}

function groupFindings(findings: ScanFinding[], groupBy: GroupBy): GroupResult {
  if (groupBy === 'severity') {
    const groups: Record<string, ScanFinding[]> = {}
    for (const f of findings) {
      if (!groups[f.severity]) groups[f.severity] = []
      groups[f.severity].push(f)
    }
    const keys = Object.keys(groups).sort(
      (a, b) => severityOrder[a as ScanFindingSeverity] - severityOrder[b as ScanFindingSeverity],
    )
    return { keys, groups, headerLabel: (k) => k }
  }

  if (groupBy === 'type') {
    const groups: Record<string, ScanFinding[]> = {}
    for (const f of findings) {
      if (!groups[f.type]) groups[f.type] = []
      groups[f.type].push(f)
    }
    const keys = Object.keys(groups).sort(
      (a, b) => typeOrder.indexOf(a as ScanFindingType) - typeOrder.indexOf(b as ScanFindingType),
    )
    return { keys, groups, headerLabel: (k) => typeLabel[k as ScanFindingType] ?? k }
  }

  if (groupBy === 'cve') {
    const groups: Record<string, ScanFinding[]> = {}
    for (const f of findings) {
      const key = f.cveId ?? UNCATEGORIZED
      if (!groups[key]) groups[key] = []
      groups[key].push(f)
    }
    const keys = Object.keys(groups).sort((a, b) => {
      if (a === UNCATEGORIZED) return 1
      if (b === UNCATEGORIZED) return -1
      return a.localeCompare(b)
    })
    return { keys, groups, headerLabel: (k) => k }
  }

  // service
  const groups: Record<string, ScanFinding[]> = {}
  for (const f of findings) {
    const key = f.serviceName ?? UNCATEGORIZED
    if (!groups[key]) groups[key] = []
    groups[key].push(f)
  }
  const keys = Object.keys(groups).sort((a, b) => {
    if (a === UNCATEGORIZED) return 1
    if (b === UNCATEGORIZED) return -1
    return a.localeCompare(b)
  })
  return { keys, groups, headerLabel: (k) => k }
}

export default function ScanFindingsView({ findings }: Props) {
  const [groupBy, setGroupBy] = useState<GroupBy>('severity')

  if (findings.length === 0) {
    return <div className="scan-findings__empty">No findings for this scan.</div>
  }

  const { keys, groups, headerLabel } = groupFindings(findings, groupBy)

  return (
    <div className="scan-findings">
      <div className="scan-findings__groupby" role="group" aria-label="Group findings by">
        {groupByOptions.map((opt) => (
          <button
            key={opt.value}
            type="button"
            className={`scan-findings__groupby-btn${groupBy === opt.value ? ' scan-findings__groupby-btn--active' : ''}`}
            onClick={() => setGroupBy(opt.value)}
          >
            {opt.label}
          </button>
        ))}
      </div>
      {keys.map((key) => {
        const groupFindingsForKey = groups[key]
        const isSeverityGroup = groupBy === 'severity'
        const headerClass = isSeverityGroup ? severityClass[key as ScanFindingSeverity] : ''
        return (
          <div key={key} className="scan-findings__group">
            <div className={`scan-findings__group-header ${headerClass}`}>
              {isSeverityGroup ? (
                <span className={`severity-badge ${headerClass}`}>{headerLabel(key)}</span>
              ) : (
                <span className="scan-findings__group-label">{headerLabel(key)}</span>
              )}
              <span className="scan-findings__group-count">
                {groupFindingsForKey.length} finding{groupFindingsForKey.length !== 1 ? 's' : ''}
              </span>
            </div>
            {groupFindingsForKey.map((finding) => (
              <div key={finding.id} className="scan-finding-card">
                <div className="scan-finding-card__header">
                  <span className="scan-finding-card__type">{typeLabel[finding.type] ?? finding.type}</span>
                  {!!finding.port && (
                    <span className="scan-finding-card__port">
                      Port {finding.port}
                      {finding.protocol ? `/${finding.protocol}` : ''}
                    </span>
                  )}
                  {finding.serviceName && (
                    <span className="scan-finding-card__service">{finding.serviceName}</span>
                  )}
                  {finding.cveId && (
                    <span className="scan-finding-card__cve">{finding.cveId}</span>
                  )}
                  {finding.cvssScore != null && (
                    <span className="scan-finding-card__cvss">CVSS {finding.cvssScore.toFixed(1)}</span>
                  )}
                </div>
                <div className="scan-finding-card__title">{finding.title}</div>
                <div className="scan-finding-card__desc">{finding.description}</div>
              </div>
            ))}
          </div>
        )
      })}
    </div>
  )
}
