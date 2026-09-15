import { describe, it, expect } from 'vitest'
import { authReducer, initialState } from '../store/authStore'
import type { AuthState } from '../store/authStore'

const base: AuthState = { status: 'unauthenticated', token: null, multiUser: false, backendUnavailable: false }

describe('authReducer — SET_MULTI_USER', () => {
  it('sets multiUser to true', () => {
    const next = authReducer(base, { type: 'SET_MULTI_USER', value: true })
    expect(next.multiUser).toBe(true)
    expect(next.status).toBe('unauthenticated')
  })

  it('sets multiUser to false', () => {
    const state: AuthState = { ...base, multiUser: true }
    const next = authReducer(state, { type: 'SET_MULTI_USER', value: false })
    expect(next.multiUser).toBe(false)
  })

  it('initial multiUser is false', () => {
    expect(initialState.multiUser).toBe(false)
  })
})

describe('authReducer — backend availability', () => {
  it('initial backendUnavailable is false', () => {
    expect(initialState.backendUnavailable).toBe(false)
  })

  it('SET_BACKEND_UNAVAILABLE sets the flag without touching status', () => {
    const next = authReducer(base, { type: 'SET_BACKEND_UNAVAILABLE', value: true })
    expect(next.backendUnavailable).toBe(true)
    expect(next.status).toBe('unauthenticated')
  })

  it('SET_BACKEND_UNAVAILABLE can clear the flag', () => {
    const state: AuthState = { ...base, backendUnavailable: true }
    const next = authReducer(state, { type: 'SET_BACKEND_UNAVAILABLE', value: false })
    expect(next.backendUnavailable).toBe(false)
  })

  it('RESET_TO_UNKNOWN sets status back to unknown', () => {
    const next = authReducer(base, { type: 'RESET_TO_UNKNOWN' })
    expect(next.status).toBe('unknown')
  })
})
