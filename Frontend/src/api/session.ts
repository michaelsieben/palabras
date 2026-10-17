import { useEffect, useSyncExternalStore } from 'react'
import { ApiError, login, logout, me, onUnauthorized } from './client'
import type { LoginCredentials, SessionUser } from './types'

interface SessionState {
  user: SessionUser | null
  checking: boolean
  error: boolean
}

let state: SessionState = { user: null, checking: true, error: false }
let started = false
let revision = 0
const listeners = new Set<() => void>()
function publish(next: SessionState) {
  state = next
  for (const listener of listeners) listener()
}
function clearSession() {
  revision++
  publish({ user: null, checking: false, error: false })
}
onUnauthorized(clearSession)

async function checkSession() {
  started = true
  const current = ++revision
  publish({ user: null, checking: true, error: false })
  try {
    const user = await me()
    if (current === revision) publish({ user, checking: false, error: false })
  } catch (error) {
    if (current === revision) publish({ user: null, checking: false, error: !(error instanceof ApiError && error.kind === 'unauthorized') })
  }
}

async function signIn(credentials: LoginCredentials) {
  const current = ++revision
  const user = await login(credentials)
  if (current === revision) publish({ user, checking: false, error: false })
}

async function signOut() {
  ++revision
  await logout()
  clearSession()
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
const snapshot = () => state

export function useSession() {
  const session = useSyncExternalStore(subscribe, snapshot)
  useEffect(() => { if (!started) void checkSession() }, [])
  return { ...session, signIn, signOut, retry: checkSession }
}
