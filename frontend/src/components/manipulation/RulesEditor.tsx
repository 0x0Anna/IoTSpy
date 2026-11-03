import { useState, useEffect } from 'react'
import ConfirmDialog from '../common/ConfirmDialog'
import type {
  ManipulationRule,
  CreateManipulationRuleRequest,
  UpdateManipulationRuleRequest,
  ManipulationRuleAction,
  ManipulationPhase,
} from '../../types/api'
import '../../styles/manipulation.css'

interface Props {
  rules: ManipulationRule[]
  loading: boolean
  error: string | null
  onAdd: (req: CreateManipulationRuleRequest) => Promise<ManipulationRule | null>
  onEdit: (id: string, req: UpdateManipulationRuleRequest) => Promise<ManipulationRule | null>
  onDelete: (id: string) => void
}

const ACTIONS: ManipulationRuleAction[] = [
  'ModifyHeader', 'ModifyBody', 'OverrideStatusCode', 'Delay', 'Drop', 'RewriteUrl', 'MatchOnly',
]
const ACTION_LABELS: Record<ManipulationRuleAction, string> = {
  ModifyHeader: 'Modify header',
  ModifyBody: 'Modify body',
  OverrideStatusCode: 'Override status',
  Delay: 'Delay',
  Drop: 'Drop',
  RewriteUrl: 'Rewrite URL',
  MatchOnly: 'Match only (chain trigger)',
}
const PHASES: ManipulationPhase[] = ['Request', 'Response']
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000'

const emptyForm: CreateManipulationRuleRequest = {
  name: '',
  enabled: true,
  priority: 0,
  phase: 'Request',
  action: 'ModifyHeader',
  hostPattern: '',
  pathPattern: '',
  methodPattern: '',
  headerName: '',
  headerValue: '',
  bodyReplace: '',
  bodyReplaceWith: '',
  overrideStatusCode: undefined,
  delayMs: undefined,
  rewriteHost: '',
  rewritePort: undefined,
  rewritePath: '',
  parentRuleId: '',
}

export default function RulesEditor({ rules, loading, error, onAdd, onEdit, onDelete }: Props) {
  const [editing, setEditing] = useState<string | null>(null) // rule id or 'new'
  const [form, setForm] = useState<CreateManipulationRuleRequest>({ ...emptyForm })
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const startNew = () => {
    setForm({ ...emptyForm })
    setEditing('new')
  }

  const startEdit = (rule: ManipulationRule) => {
    setForm({
      name: rule.name,
      enabled: rule.enabled,
      priority: rule.priority,
      phase: rule.phase,
      action: rule.action,
      hostPattern: rule.hostPattern ?? '',
      pathPattern: rule.pathPattern ?? '',
      methodPattern: rule.methodPattern ?? '',
      headerName: rule.headerName ?? '',
      headerValue: rule.headerValue ?? '',
      bodyReplace: rule.bodyReplace ?? '',
      bodyReplaceWith: rule.bodyReplaceWith ?? '',
      overrideStatusCode: rule.overrideStatusCode,
      delayMs: rule.delayMs,
      rewriteHost: rule.rewriteHost ?? '',
      rewritePort: rule.rewritePort,
      rewritePath: rule.rewritePath ?? '',
      parentRuleId: rule.parentRuleId ?? '',
    })
    setEditing(rule.id)
  }

  const cancelEdit = () => {
    setEditing(null)
    setForm({ ...emptyForm })
  }

  const handleSave = async () => {
    if (!form.name.trim()) return
    // RewriteUrl only makes sense before the request is forwarded.
    const base: CreateManipulationRuleRequest = {
      ...form,
      phase: effectivePhase,
      // The inputs display these defaults, so send them rather than leaving the rule a no-op.
      overrideStatusCode: form.action === 'OverrideStatusCode' ? (form.overrideStatusCode ?? 200) : form.overrideStatusCode,
      delayMs: form.action === 'Delay' ? (form.delayMs ?? 1000) : form.delayMs,
    }
    if (editing === 'new') {
      const result = await onAdd({ ...base, parentRuleId: base.parentRuleId || undefined })
      if (result) cancelEdit()
    } else if (editing) {
      // The API treats a missing parentRuleId as "unchanged"; the empty GUID clears it.
      const result = await onEdit(editing, {
        ...base, id: editing, parentRuleId: base.parentRuleId || EMPTY_GUID,
      } as UpdateManipulationRuleRequest)
      if (result) cancelEdit()
    }
  }

  // Candidate chain parents: any other rule, except response rules when this one is a
  // request rule (a request rule can't wait on a response match that hasn't happened yet).
  const effectivePhase: ManipulationPhase = form.action === 'RewriteUrl' ? 'Request' : (form.phase ?? 'Request')
  const parentCandidates = rules.filter((r) =>
    r.id !== editing && !(effectivePhase === 'Request' && r.phase === 'Response'))
  const ruleName = (id?: string | null) => rules.find((r) => r.id === id)?.name

  // Keyboard shortcuts for the active form
  useEffect(() => {
    if (!editing) return
    const handle = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); cancelEdit() }
      if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); void handleSave() }
    }
    document.addEventListener('keydown', handle)
    return () => document.removeEventListener('keydown', handle)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editing, form])

  const updateField = <K extends keyof CreateManipulationRuleRequest>(
    key: K,
    value: CreateManipulationRuleRequest[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  const confirmingRule = confirmDeleteId ? rules.find((r) => r.id === confirmDeleteId) : null

  return (
    <div className="manip-section">
      {confirmDeleteId && confirmingRule && (
        <ConfirmDialog
          title="Delete rule"
          message={`Delete "${confirmingRule.name}"? This cannot be undone.`}
          confirmLabel="Delete"
          danger
          onConfirm={() => { onDelete(confirmDeleteId); setConfirmDeleteId(null) }}
          onCancel={() => setConfirmDeleteId(null)}
        />
      )}

      <div className="manip-section__header">
        <span className="manip-section__title">
          Manipulation Rules {loading && <span className="manip-spinner" />}
        </span>
        <button className="manip-btn manip-btn--primary" onClick={startNew}>
          Add Rule
        </button>
      </div>

      {error && <div className="manip-error">{error}</div>}

      {/* Edit / Create form */}
      {editing && (
        <div className="manip-form">
          <div className="manip-form__row">
            <label className="manip-form__label">
              Name
              <input
                className="manip-form__input"
                value={form.name}
                onChange={(e) => updateField('name', e.target.value)}
                placeholder="Rule name"
                autoFocus
              />
            </label>
            <label className="manip-form__label">
              Priority
              <input
                className="manip-form__input manip-form__input--narrow"
                type="number"
                value={form.priority ?? 0}
                onChange={(e) => updateField('priority', Number(e.target.value))}
              />
            </label>
            <label className="manip-form__label">
              Phase
              <select
                className="manip-form__select"
                value={form.action === 'RewriteUrl' ? 'Request' : form.phase}
                disabled={form.action === 'RewriteUrl'}
                onChange={(e) => updateField('phase', e.target.value as ManipulationPhase)}
              >
                {PHASES.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </label>
            <label className="manip-form__label">
              Action
              <select
                className="manip-form__select"
                value={form.action}
                onChange={(e) => updateField('action', e.target.value as ManipulationRuleAction)}
              >
                {ACTIONS.map((a) => <option key={a} value={a}>{ACTION_LABELS[a]}</option>)}
              </select>
            </label>
            <label className="manip-form__checkbox">
              <input
                type="checkbox"
                checked={form.enabled ?? true}
                onChange={(e) => updateField('enabled', e.target.checked)}
              />
              Enabled
            </label>
          </div>

          <div className="manip-form__row">
            <label className="manip-form__label">
              Host Pattern
              <input
                className="manip-form__input"
                value={form.hostPattern ?? ''}
                onChange={(e) => updateField('hostPattern', e.target.value)}
                placeholder=".*example\.com"
              />
            </label>
            <label className="manip-form__label">
              Path Pattern
              <input
                className="manip-form__input"
                value={form.pathPattern ?? ''}
                onChange={(e) => updateField('pathPattern', e.target.value)}
                placeholder="/api/.*"
              />
            </label>
            <label className="manip-form__label">
              Method Pattern
              <input
                className="manip-form__input"
                value={form.methodPattern ?? ''}
                onChange={(e) => updateField('methodPattern', e.target.value)}
                placeholder="GET|POST"
              />
            </label>
            <label className="manip-form__label">
              Chain after
              <select
                className="manip-form__select"
                value={form.parentRuleId ?? ''}
                onChange={(e) => updateField('parentRuleId', e.target.value)}
                aria-label="Chain after"
                title="Only fire when the selected rule matched the same request/response exchange"
              >
                <option value="">— none —</option>
                {parentCandidates.map((r) => (
                  <option key={r.id} value={r.id}>{r.name} ({r.phase} · {ACTION_LABELS[r.action] ?? r.action})</option>
                ))}
              </select>
            </label>
          </div>

          {(form.action === 'RewriteUrl') && (
            <div className="manip-form__row">
              <label className="manip-form__label">
                Rewrite Host
                <input
                  className="manip-form__input"
                  value={form.rewriteHost ?? ''}
                  onChange={(e) => updateField('rewriteHost', e.target.value)}
                  placeholder="www.bing.com  (or $1.example.com)"
                />
              </label>
              <label className="manip-form__label">
                Rewrite Port
                <input
                  className="manip-form__input manip-form__input--narrow"
                  type="number"
                  value={form.rewritePort ?? ''}
                  onChange={(e) => updateField('rewritePort', e.target.value === '' ? undefined : Number(e.target.value))}
                  min={1}
                  max={65535}
                  placeholder="keep"
                />
              </label>
              <label className="manip-form__label">
                Rewrite Path
                <input
                  className="manip-form__input"
                  value={form.rewritePath ?? ''}
                  onChange={(e) => updateField('rewritePath', e.target.value)}
                  placeholder="/v2/$1?debug=1  (blank = keep)"
                />
              </label>
              <span className="manip-form__hint">
                $1, $2… refer to groups captured by the Host / Path patterns. Scheme (http/https) is not changed.
              </span>
            </div>
          )}

          {(form.action === 'MatchOnly') && (
            <div className="manip-form__row">
              <span className="manip-form__hint">
                Changes nothing on its own. Create Response-phase rules with “Chain after” set to this rule
                to modify the response of every request it matches.
              </span>
            </div>
          )}

          {(form.action === 'ModifyHeader') && (
            <div className="manip-form__row">
              <label className="manip-form__label">
                Header Name
                <input
                  className="manip-form__input"
                  value={form.headerName ?? ''}
                  onChange={(e) => updateField('headerName', e.target.value)}
                  placeholder="X-Custom-Header"
                />
              </label>
              <label className="manip-form__label">
                Header Value
                <input
                  className="manip-form__input"
                  value={form.headerValue ?? ''}
                  onChange={(e) => updateField('headerValue', e.target.value)}
                  placeholder="value"
                />
              </label>
            </div>
          )}

          {(form.action === 'ModifyBody') && (
            <div className="manip-form__row">
              <label className="manip-form__label">
                Body Replace (pattern)
                <input
                  className="manip-form__input"
                  value={form.bodyReplace ?? ''}
                  onChange={(e) => updateField('bodyReplace', e.target.value)}
                  placeholder="search string"
                />
              </label>
              <label className="manip-form__label">
                Replace With
                <input
                  className="manip-form__input"
                  value={form.bodyReplaceWith ?? ''}
                  onChange={(e) => updateField('bodyReplaceWith', e.target.value)}
                  placeholder="replacement string"
                />
              </label>
            </div>
          )}

          {(form.action === 'OverrideStatusCode') && (
            <div className="manip-form__row">
              <label className="manip-form__label">
                Status Code
                <input
                  className="manip-form__input manip-form__input--narrow"
                  type="number"
                  value={form.overrideStatusCode ?? 200}
                  onChange={(e) => updateField('overrideStatusCode', Number(e.target.value))}
                  min={100}
                  max={599}
                />
              </label>
            </div>
          )}

          {(form.action === 'Delay') && (
            <div className="manip-form__row">
              <label className="manip-form__label">
                Delay (ms)
                <input
                  className="manip-form__input manip-form__input--narrow"
                  type="number"
                  value={form.delayMs ?? 1000}
                  onChange={(e) => updateField('delayMs', Number(e.target.value))}
                  min={0}
                />
              </label>
            </div>
          )}

          <div className="manip-form__actions">
            <button className="manip-btn manip-btn--primary" onClick={handleSave}>
              {editing === 'new' ? 'Create' : 'Save'}
            </button>
            <button className="manip-btn manip-btn--ghost" onClick={cancelEdit}>
              Cancel
            </button>
            <span className="manip-form__hint">Ctrl+S to save · Esc to cancel</span>
          </div>
        </div>
      )}

      {/* Rules list */}
      <div className="manip-table">
        {rules.length === 0 && !editing ? (
          <div className="manip-empty">
            <div className="manip-empty__message">No manipulation rules yet.</div>
            <div className="manip-empty__hint">Add a rule to modify headers, body, status codes, or inject delays on matching traffic.</div>
          </div>
        ) : (
          rules.map((rule) => (
            <div key={rule.id} className={`manip-row ${!rule.enabled ? 'manip-row--disabled' : ''}`}>
              <div className="manip-row__main">
                <span className={`manip-badge manip-badge--${rule.action.toLowerCase()}`}>
                  {ACTION_LABELS[rule.action] ?? rule.action}
                </span>
                <span className={`manip-phase manip-phase--${rule.phase.toLowerCase()}`}>
                  {rule.phase}
                </span>
                <span className="manip-row__name">{rule.name}</span>
                {!rule.enabled && <span className="manip-row__disabled-tag">disabled</span>}
              </div>
              <div className="manip-row__patterns">
                {rule.hostPattern && <code className="manip-pattern">{rule.hostPattern}</code>}
                {rule.pathPattern && <code className="manip-pattern">{rule.pathPattern}</code>}
                {rule.methodPattern && <code className="manip-pattern">{rule.methodPattern}</code>}
                {rule.action === 'RewriteUrl' && (
                  <code className="manip-pattern">
                    → {[rule.rewriteHost, rule.rewritePort && `:${rule.rewritePort}`, rule.rewritePath].filter(Boolean).join('')}
                  </code>
                )}
                {rule.parentRuleId && (
                  <span className="manip-pattern" title="Chained rule">
                    ⛓ after {ruleName(rule.parentRuleId) ?? 'missing rule'}
                  </span>
                )}
              </div>
              <div className="manip-row__actions">
                <button className="manip-btn manip-btn--small" onClick={() => startEdit(rule)}>
                  Edit
                </button>
                <button
                  className="manip-btn manip-btn--small manip-btn--danger"
                  onClick={() => setConfirmDeleteId(rule.id)}
                >
                  Delete
                </button>
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  )
}
