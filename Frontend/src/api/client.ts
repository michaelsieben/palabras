import type { ApiErrorKind, LoginCredentials, SessionUser } from './types'

export class ApiError extends Error {
  readonly kind: ApiErrorKind
  readonly status: number | undefined
  readonly body: unknown

  constructor(kind: ApiErrorKind, status?: number, body?: unknown) {
    super(`API: ${kind}`)
    this.name = 'ApiError'
    this.kind = kind
    this.status = status
    this.body = body
  }
}

const unauthorizedListeners = new Set<() => void>()

export function onUnauthorized(listener: () => void) {
  unauthorizedListeners.add(listener)
  return () => { unauthorizedListeners.delete(listener) }
}

// Relative API paths only: credentials must never be sent to another origin.
export async function apiRequest(path: `/api/${string}`, init: RequestInit = {}): Promise<unknown> {
  if (!path.startsWith('/api/') || path.includes('\\')) throw new ApiError('invalid-response')
  const method = (init.method ?? 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (method !== 'GET') headers.set('X-Palabras', '1')
  if (init.body !== undefined && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  let response: Response
  try {
    response = await fetch(path, { ...init, method, headers, credentials: 'include', redirect: 'error', cache: 'no-store' })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError('network')
  }
  // Invalidate before parsing: even a malformed 401 must end the UI session.
  if (response.status === 401) for (const listener of unauthorizedListeners) listener()
  let body: unknown
  if (response.status !== 204) {
    try { body = await response.json() } catch {
      if (response.ok) throw new ApiError('invalid-response', response.status)
    }
  }
  if (!response.ok) throw new ApiError(response.status === 401 ? 'unauthorized' : response.status === 403 ? 'forbidden' : 'http', response.status, body)
  return body
}

function sessionUser(body: unknown): SessionUser {
  if (typeof body !== 'object' || body === null || !('username' in body) || typeof body.username !== 'string' || !('role' in body) || (body.role !== 'owner' && body.role !== 'reviewer')) throw new ApiError('invalid-response')
  return { username: body.username, role: body.role }
}

export async function login(credentials: LoginCredentials): Promise<SessionUser> {
  return sessionUser(await apiRequest('/api/auth/login', { method: 'POST', body: JSON.stringify(credentials) }))
}

export async function logout(): Promise<void> {
  await apiRequest('/api/auth/logout', { method: 'POST' })
}

export async function me(): Promise<SessionUser> {
  return sessionUser(await apiRequest('/api/auth/me'))
}
