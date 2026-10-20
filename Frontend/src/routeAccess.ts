import type { SessionUser } from './api/types'

export type RouteOutcome = 'allow' | 'login' | 'forbidden'

export function routeAccess(path: string, user: SessionUser | null): RouteOutcome {
  if (path === '/login' || path === '/debug/viewport') return 'allow'
  if (!user) return 'login'
  if ((path === '/revisar' || path === '/aprobadas') && user.role !== 'reviewer') return 'forbidden'
  if (path === '/ajustes/recordatorio' && user.role !== 'owner') return 'forbidden'
  return 'allow'
}

// Preserve only the two known notification handoffs, never arbitrary query URLs.
export function loginDestination(search: string, user: SessionUser): string {
  const next = new URLSearchParams(search).get('next')
  const home = user.role === 'reviewer' ? '/revisar' : '/'
  if (user.role === 'owner' && next && !/[\\#]/.test(next)) {
    if (next === '/?start=1') return next
    if (/^\/ajustes\/recordatorio(?:\?from=notif&d=\d{4}-\d{2}-\d{2})?$/.test(next)) return next
  }
  return next && next.startsWith('/') && !next.startsWith('//') && !/[\\?#]/.test(next) && next !== '/login' && next !== '/' && next !== '/sesion' && routeAccess(next, user) === 'allow' ? next : home
}

export function consumeStart(search: string): string | null {
  const params = new URLSearchParams(search)
  if (params.get('start') !== '1') return null
  params.delete('start')
  return '/' + (params.size ? '?' + params.toString() : '')
}
