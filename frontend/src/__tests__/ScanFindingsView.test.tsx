import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect } from 'vitest'
import ScanFindingsView from '../components/scanner/ScanFindingsView'
import type { ScanFinding } from '../types/api'

function makeFinding(overrides: Partial<ScanFinding>): ScanFinding {
  return {
    id: `finding-${Math.random()}`,
    scanJobId: 'job-1',
    type: 'OpenPort',
    severity: 'Medium',
    title: 'Default title',
    description: 'Default description',
    foundAt: '2026-01-01T00:00:00Z',
    ...overrides,
  }
}

const findings: ScanFinding[] = [
  makeFinding({ id: 'f-critical-cve', type: 'Cve', severity: 'Critical', cveId: 'CVE-2023-1111', serviceName: 'ssh', title: 'Critical CVE' }),
  makeFinding({ id: 'f-high-noport', type: 'ConfigIssue', severity: 'High', title: 'Config issue with no port/service/cve' }),
  makeFinding({ id: 'f-info-port', type: 'OpenPort', severity: 'Info', port: 80, protocol: 'tcp', serviceName: 'http', title: 'Open port 80' }),
]

describe('ScanFindingsView', () => {
  it('renders "no findings" empty state', () => {
    render(<ScanFindingsView findings={[]} />)
    expect(screen.getByText('No findings for this scan.')).toBeInTheDocument()
  })

  it('groups by severity by default, ordered Critical -> Info', () => {
    render(<ScanFindingsView findings={findings} />)
    const headers = screen.getAllByText(/^(Critical|High|Medium|Low|Info)$/)
    expect(headers.map((h) => h.textContent)).toEqual(['Critical', 'High', 'Info'])
  })

  it('switches to Type grouping and shows type buckets with display labels', async () => {
    const user = userEvent.setup()
    const { container } = render(<ScanFindingsView findings={findings} />)
    await user.click(screen.getByRole('button', { name: 'Type' }))

    const groupLabels = Array.from(container.querySelectorAll('.scan-findings__group-label')).map(
      (el) => el.textContent,
    )
    expect(groupLabels).toEqual(['Open Port', 'CVE', 'Config Issue'])
  })

  it('switches to CVE grouping and puts findings without a CveId in an "Uncategorized" bucket sorted last', async () => {
    const user = userEvent.setup()
    const { container } = render(<ScanFindingsView findings={findings} />)
    await user.click(screen.getByRole('button', { name: 'CVE' }))

    const groupLabels = Array.from(container.querySelectorAll('.scan-findings__group-label')).map(
      (el) => el.textContent,
    )
    expect(groupLabels).toEqual(['CVE-2023-1111', 'Uncategorized'])
  })

  it('switches to Service grouping and puts findings without a ServiceName in an "Uncategorized" bucket sorted last', async () => {
    const user = userEvent.setup()
    const { container } = render(<ScanFindingsView findings={findings} />)
    await user.click(screen.getByRole('button', { name: 'Service' }))

    const groupLabels = Array.from(container.querySelectorAll('.scan-findings__group-label')).map(
      (el) => el.textContent,
    )
    expect(groupLabels).toEqual(['http', 'ssh', 'Uncategorized'])
  })

  it('does not render the removed evidence/remediation sections', () => {
    render(<ScanFindingsView findings={findings} />)
    expect(screen.queryByText('Evidence:')).not.toBeInTheDocument()
    expect(screen.queryByText('Remediation:')).not.toBeInTheDocument()
  })

  it('renders a CVSS badge when cvssScore is present', () => {
    render(
      <ScanFindingsView
        findings={[makeFinding({ type: 'Cve', cveId: 'CVE-2024-9999', cvssScore: 9.8, title: 'Critical CVE' })]}
      />,
    )
    expect(screen.getByText('CVSS 9.8')).toBeInTheDocument()
  })
})
