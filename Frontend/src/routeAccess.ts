import type { SessionUser } from './api/types'

export type RouteOutcome = 'allow' | 'login' | 'forbidden'

export function routeAccess(path: string, user: SessionUser | null): RouteOutcome {
  if (path === '/login') return 'allow'
  if (!user) return 'login'
  if ((path === '/revisar' || path === '/aprobadas') && !user.roles.includes('reviewer')) return 'forbidden'
  return 'allow'
}

// Only route pathnames, never external URLs or a recursive login destination.
export function loginDestination(search: string): string {
  const next = new URLSearchParams(search).get('next')
  return next && next.startsWith('/') && !next.startsWith('//') && !/[\\?#]/.test(next) && next !== '/login' ? next : '/'
}
