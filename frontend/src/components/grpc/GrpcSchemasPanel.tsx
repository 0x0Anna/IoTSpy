import { useState } from 'react'
import { useGrpcSchemas } from '../../hooks/useGrpcSchemas'
import type { ProtoSchema } from '../../api/grpcSchemas'

export default function GrpcSchemasPanel() {
  const { schemas, loading, error, addSchema, removeSchema } = useGrpcSchemas()

  const [showAdd, setShowAdd] = useState(false)
  const [formName, setFormName] = useState('')
  const [formProto, setFormProto] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)

  const resetForm = () => {
    setFormName('')
    setFormProto('')
    setSubmitError(null)
    setShowAdd(false)
  }

  const handleAdd = async () => {
    if (!formName.trim() || !formProto.trim()) return
    setSubmitting(true)
    setSubmitError(null)
    try {
      await addSchema(formName.trim(), formProto.trim())
      resetForm()
    } catch (e) {
      setSubmitError(e instanceof Error ? e.message : 'Upload failed')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div style={{ padding: 16, maxWidth: 800 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
        <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--color-text)' }}>
          gRPC Proto Schemas
        </div>
        <button className="btn btn--primary" onClick={() => setShowAdd(true)} style={{ fontSize: 12 }}>
          + Upload Schema
        </button>
      </div>

      <div style={{ fontSize: 12, color: 'var(--color-text-secondary)', marginBottom: 16 }}>
        Upload <code>.proto</code> files to resolve field numbers to names in decoded gRPC captures.
        Schemas are applied automatically during capture decoding.
      </div>

      {error && (
        <div style={{ padding: 8, background: '#3a1a1a', color: '#fca5a5', borderRadius: 4, marginBottom: 8, fontSize: 12 }}>
          {error}
        </div>
      )}

      {showAdd && (
        <div style={{ marginBottom: 16, padding: 12, background: 'var(--color-surface)', borderRadius: 6, border: '1px solid var(--color-border)' }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <label style={{ fontSize: 12, color: 'var(--color-text-secondary)' }}>
              Schema Name
              <input
                value={formName}
                onChange={e => setFormName(e.target.value)}
                placeholder="e.g. DeviceService v1"
                style={{ display: 'block', width: '100%', marginTop: 3 }}
              />
            </label>
            <label style={{ fontSize: 12, color: 'var(--color-text-secondary)' }}>
              Proto file content
              <textarea
                value={formProto}
                onChange={e => setFormProto(e.target.value)}
                placeholder={'syntax = "proto3";\n\nmessage MyMessage {\n  string name = 1;\n  int32 value = 2;\n}'}
                rows={10}
                style={{ display: 'block', width: '100%', marginTop: 3, fontFamily: 'monospace', fontSize: 12 }}
              />
            </label>
          </div>
          {submitError && (
            <div style={{ color: 'var(--color-error)', fontSize: 12, marginTop: 6 }}>{submitError}</div>
          )}
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <button
              className="btn btn--primary"
              onClick={() => void handleAdd()}
              disabled={!formName.trim() || !formProto.trim() || submitting}
            >
              {submitting ? 'Uploading…' : 'Upload'}
            </button>
            <button className="btn btn--secondary" onClick={resetForm}>Cancel</button>
          </div>
        </div>
      )}

      {loading && !schemas.length ? (
        <div style={{ color: 'var(--color-text-secondary)', fontSize: 13 }}>Loading…</div>
      ) : schemas.length === 0 ? (
        <div style={{ color: 'var(--color-text-secondary)', fontSize: 13, fontStyle: 'italic' }}>
          No proto schemas uploaded yet. Upload a <code>.proto</code> file to enrich gRPC field decoding.
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          {schemas.map((s: ProtoSchema) => (
            <SchemaRow
              key={s.id}
              schema={s}
              expanded={expanded === s.id}
              onToggle={() => setExpanded(expanded === s.id ? null : s.id)}
              onDelete={() => void removeSchema(s.id)}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function SchemaRow({
  schema,
  expanded,
  onToggle,
  onDelete,
}: {
  schema: ProtoSchema
  expanded: boolean
  onToggle: () => void
  onDelete: () => void
}) {
  let fieldCount = 0
  try {
    fieldCount = Object.keys(JSON.parse(schema.fieldMapJson || '{}')).length
  } catch {
    fieldCount = 0
  }

  return (
    <div style={{ border: '1px solid var(--color-border)', borderRadius: 4, overflow: 'hidden' }}>
      <div
        style={{ display: 'flex', alignItems: 'center', padding: '8px 12px', gap: 8, cursor: 'pointer', background: 'var(--color-surface)' }}
        onClick={onToggle}
      >
        <span style={{ fontSize: 13, fontWeight: 500, flex: 1, color: 'var(--color-text)' }}>{schema.name}</span>
        <span style={{ fontSize: 11, color: 'var(--color-text-secondary)' }}>{fieldCount} field{fieldCount !== 1 ? 's' : ''}</span>
        <span style={{ fontSize: 11, color: 'var(--color-text-secondary)' }}>
          {new Date(schema.createdAt).toLocaleDateString()}
        </span>
        <button
          className="btn btn--danger"
          style={{ fontSize: 11, padding: '2px 8px' }}
          onClick={e => { e.stopPropagation(); onDelete() }}
        >
          Delete
        </button>
        <span style={{ fontSize: 11, color: 'var(--color-text-secondary)' }}>{expanded ? '▲' : '▼'}</span>
      </div>
      {expanded && (
        <pre style={{ margin: 0, padding: '8px 12px', fontSize: 11, background: 'var(--color-bg)', overflowX: 'auto', color: 'var(--color-text)', borderTop: '1px solid var(--color-border)' }}>
          {schema.rawProto}
        </pre>
      )}
    </div>
  )
}
