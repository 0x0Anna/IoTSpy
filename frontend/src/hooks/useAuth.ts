import { useEffect } from 'react'
import { useNavigate, useLocation } from 'react-router-dom'
import { getToken, setOnUnauthorized, NetworkError } from '../api/client'
import { getAuthStatus, getMe, login as apiLogin, setup as apiSetup } from '../api/auth'
import {
  dispatchLogin,
  useAuthDispatch,
  useAuthState,
  type AuthState,
} from '../store/authStore'
import type { CurrentUser, LoginRequest, SetupRequest } from '../types/api'

const CURRENT_USER_KEY = 'iotspy-user'

export function useCurrentUser(): CurrentUser | null {
  const raw = typeof localStorage !== 'undefined' ? localStorage.getItem(CURRENT_USER_KEY) : null
  if (!raw) return null
  try {
    return JSON.parse(raw) as CurrentUser
  } catch {
    return null
  }
}

export function useAuthInit(): AuthState {
  const dispatch = useAuthDispatch()
  const state = useAuthState()
  const navigate = useNavigate()
  const location = useLocation()

  useEffect(() => {
    let cancelled = false
    async function init() {
      try {
        const { passwordSet, multiUser } = await getAuthStatus()
        if (cancelled) return
        dispatch({ type: 'SET_BACKEND_UNAVAILABLE', value: false })
        dispatch({ type: 'SET_MULTI_USER', value: multiUser ?? false })
        if (!passwordSet) {
          dispatch({ type: 'SET_NO_PASSWORD' })
          navigate('/setup', { replace: true })
          return
        }
        const token = getToken()
        if (!token) {
          dispatch({ type: 'SET_UNAUTHENTICATED' })
          navigate('/login', { replace: true })
          return
        }
        dispatch({ type: 'SET_AUTHENTICATED', token })
        setOnUnauthorized(() => {
          dispatch({ type: 'LOGOUT' })
          navigate('/login', { replace: true })
        })
        // Ensure user profile is in localStorage (missing for legacy/pre-multiuser logins)
        if (!localStorage.getItem(CURRENT_USER_KEY)) {
          try {
            const { user } = await getMe()
            localStorage.setItem(CURRENT_USER_KEY, JSON.stringify(user))
          } catch { /* non-fatal — wrench icon just won't show */ }
        }
        // Only redirect to / if currently on an unauthenticated page
        if (['/login', '/setup'].includes(location.pathname)) {
          navigate('/', { replace: true })
        }
      } catch (err) {
        if (!cancelled) {
          // Distinguish "server is unreachable" from a normal logged-out state so
          // the login page can show a banner instead of just failing the submit.
          if (err instanceof NetworkError) {
            dispatch({ type: 'SET_BACKEND_UNAVAILABLE', value: true })
          }
          dispatch({ type: 'SET_UNAUTHENTICATED' })
          navigate('/login', { replace: true })
        }
      }
    }
    if (state.status === 'unknown') {
      void init()
    }
    return () => {
      cancelled = true
    }
  }, [state.status, dispatch, navigate, location.pathname])

  // While the backend was unreachable, poll until it's back, then re-run init.
  useEffect(() => {
    if (!state.backendUnavailable) return
    let cancelled = false
    const id = setInterval(async () => {
      const reachable = await getAuthStatus().then(() => true).catch(() => false)
      if (cancelled || !reachable) return
      dispatch({ type: 'SET_BACKEND_UNAVAILABLE', value: false })
      dispatch({ type: 'RESET_TO_UNKNOWN' })
    }, 5_000)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [state.backendUnavailable, dispatch])

  return state
}

export function useLogin() {
  const dispatch = useAuthDispatch()
  const navigate = useNavigate()

  return async (req: LoginRequest) => {
    const { token, user } = await apiLogin(req)
    if (user) {
      localStorage.setItem(CURRENT_USER_KEY, JSON.stringify(user))
    }
    dispatchLogin(dispatch, token)
    navigate('/', { replace: true })
  }
}

export function useSetup() {
  const navigate = useNavigate()

  return async (req: SetupRequest) => {
    await apiSetup(req)
    navigate('/login', { replace: true })
  }
}

export function useLogout() {
  const dispatch = useAuthDispatch()
  const navigate = useNavigate()

  return () => {
    localStorage.removeItem(CURRENT_USER_KEY)
    dispatch({ type: 'LOGOUT' })
    navigate('/login', { replace: true })
  }
}
