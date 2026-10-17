import type { SessionUser } from './api/types'

export type RouteOutcome = 'allow' | 'login' | 'forbidden'

export function routeAccess(path: string, user: SessionUser | null): RouteOutcome {
  if (path === '/login' || path === '/debug/viewport') return 'allow'
  if (!user) return 'login'
  if ((path === '/revisar' || path === '/aprobadas') && user.role !== 'reviewer') return 'forbidden'
  return 'allow'
}

// Only route pathnames, never external URLs or a recursive login destination.
export function loginDestination(search: string, user: SessionUser): string {
  const next = new URLSearchParams(search).get('next')
  const home = user.role === 'reviewer' ? '/revisar' : '/'
  return next && next.startsWith('/') && !next.startsWith('//') && !/[\\?#]/.test(next) && next !== '/login' && next !== '/' && next !== '/sesion' && routeAccess(next, user) === 'allow' ? next : home
}
