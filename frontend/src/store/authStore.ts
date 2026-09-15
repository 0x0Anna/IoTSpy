import {
  createContext,
  useContext,
  useReducer,
  type Dispatch,
  type ReactNode,
} from 'react'
import { clearToken, setToken } from '../api/client'

// ── State ────────────────────────────────────────────────────────────────────

export type AuthStatus = 'unknown' | 'no-password' | 'unauthenticated' | 'authenticated'

export interface AuthState {
  status: AuthStatus
  token: string | null
  multiUser: boolean
  // Set when the initial auth-status check couldn't reach the backend at all
  // (as opposed to a normal 401/logged-out state). Cleared once it's reachable again.
  backendUnavailable: boolean
}

export const initialState: AuthState = {
  status: 'unknown',
  token: null,
  multiUser: false,
  backendUnavailable: false,
}

// ── Actions ───────────────────────────────────────────────────────────────────

type AuthAction =
  | { type: 'SET_NO_PASSWORD' }
  | { type: 'SET_UNAUTHENTICATED' }
  | { type: 'SET_AUTHENTICATED'; token: string }
  | { type: 'SET_MULTI_USER'; value: boolean }
  | { type: 'SET_BACKEND_UNAVAILABLE'; value: boolean }
  | { type: 'RESET_TO_UNKNOWN' }
  | { type: 'LOGOUT' }

export function authReducer(state: AuthState, action: AuthAction): AuthState {
  switch (action.type) {
    case 'SET_NO_PASSWORD':
      return { ...state, status: 'no-password', token: null }
    case 'SET_UNAUTHENTICATED':
      return { ...state, status: 'unauthenticated', token: null }
    case 'SET_AUTHENTICATED':
      return { ...state, status: 'authenticated', token: action.token }
    case 'SET_MULTI_USER':
      return { ...state, multiUser: action.value }
    case 'SET_BACKEND_UNAVAILABLE':
      return { ...state, backendUnavailable: action.value }
    case 'RESET_TO_UNKNOWN':
      return { ...state, status: 'unknown' }
    case 'LOGOUT':
      clearToken()
      return { ...state, status: 'unauthenticated', token: null }
    default:
      return state
  }
}

// ── Context ───────────────────────────────────────────────────────────────────

const AuthStateCtx = createContext<AuthState>(initialState)
const AuthDispatchCtx = createContext<Dispatch<AuthAction>>(() => undefined)

import { createElement } from 'react'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, dispatch] = useReducer(authReducer, initialState)
  return createElement(
    AuthStateCtx.Provider,
    { value: state },
    createElement(AuthDispatchCtx.Provider, { value: dispatch }, children),
  )
}

export function useAuthState(): AuthState {
  return useContext(AuthStateCtx)
}

export function useAuthDispatch(): Dispatch<AuthAction> {
  return useContext(AuthDispatchCtx)
}

// ── Helpers for dispatch consumers ────────────────────────────────────────────

export function dispatchLogin(dispatch: Dispatch<AuthAction>, token: string): void {
  setToken(token)
  dispatch({ type: 'SET_AUTHENTICATED', token })
}
