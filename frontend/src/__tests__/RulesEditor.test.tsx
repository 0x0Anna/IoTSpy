import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import RulesEditor from '../components/manipulation/RulesEditor'
import type { ManipulationRule } from '../types/api'

const rule = (over: Partial<ManipulationRule>): ManipulationRule => ({
  id: 'r1', name: 'Rule', enabled: true, priority: 0, phase: 'Request', action: 'MatchOnly',
  createdAt: '', updatedAt: '', ...over,
})

const setup = (rules: ManipulationRule[] = []) => {
  const onAdd = vi.fn().mockResolvedValue(rules[0] ?? rule({}))
  const onEdit = vi.fn().mockResolvedValue(rules[0] ?? rule({}))
  render(<RulesEditor rules={rules} loading={false} error={null} onAdd={onAdd} onEdit={onEdit} onDelete={vi.fn()} />)
  return { onAdd, onEdit }
}

describe('RulesEditor', () => {
  it('creates a RewriteUrl rule with the rewrite host, forcing the Request phase', async () => {
    const user = userEvent.setup()
    const { onAdd } = setup()

    await user.click(screen.getByRole('button', { name: 'Add Rule' }))
    await user.type(screen.getByPlaceholderText('Rule name'), 'bing to www')
    await user.selectOptions(screen.getByLabelText('Action'), 'RewriteUrl')
    await user.type(screen.getByPlaceholderText('.*example\\.com'), '^bing\\.com$')
    await user.type(screen.getByLabelText('Rewrite Host'), 'www.bing.com')
    await user.click(screen.getByRole('button', { name: 'Create' }))

    expect(onAdd).toHaveBeenCalledWith(expect.objectContaining({
      action: 'RewriteUrl', phase: 'Request', hostPattern: '^bing\\.com$', rewriteHost: 'www.bing.com',
    }))
    expect(onAdd.mock.calls[0][0].parentRuleId).toBeUndefined()
  })

  it('chains a response status rule after a request trigger and sends the displayed default status', async () => {
    const user = userEvent.setup()
    const trigger = rule({ id: 'trigger-id', name: 'POST yandex', methodPattern: '^POST$', hostPattern: 'yandex\\.net$' })
    const { onAdd } = setup([trigger])

    await user.click(screen.getByRole('button', { name: 'Add Rule' }))
    await user.type(screen.getByPlaceholderText('Rule name'), 'yandex 200')
    await user.selectOptions(screen.getByLabelText('Phase'), 'Response')
    await user.selectOptions(screen.getByLabelText('Action'), 'OverrideStatusCode')
    await user.selectOptions(screen.getByLabelText('Chain after'), 'trigger-id')
    await user.click(screen.getByRole('button', { name: 'Create' }))

    expect(onAdd).toHaveBeenCalledWith(expect.objectContaining({
      action: 'OverrideStatusCode', phase: 'Response', overrideStatusCode: 200, parentRuleId: 'trigger-id',
    }))
  })

  it('does not offer response rules as parents of a request rule', async () => {
    const user = userEvent.setup()
    setup([rule({ id: 'resp', name: 'Resp rule', phase: 'Response', action: 'ModifyHeader' })])

    await user.click(screen.getByRole('button', { name: 'Add Rule' }))

    expect(screen.queryByRole('option', { name: /Resp rule/ })).not.toBeInTheDocument()
  })

  it('clears the chain on edit by sending the empty GUID', async () => {
    const user = userEvent.setup()
    const parent = rule({ id: 'p', name: 'Parent' })
    const child = rule({ id: 'c', name: 'Child', phase: 'Response', action: 'ModifyHeader', parentRuleId: 'p' })
    const { onEdit } = setup([parent, child])

    expect(screen.getByText(/after Parent/)).toBeInTheDocument()
    await user.click(screen.getAllByRole('button', { name: 'Edit' })[1])
    await user.selectOptions(screen.getByLabelText('Chain after'), '')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(onEdit).toHaveBeenCalledWith('c', expect.objectContaining({
      parentRuleId: '00000000-0000-0000-0000-000000000000',
    }))
  })
})
