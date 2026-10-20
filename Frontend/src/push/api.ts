import { ApiError, apiRequest } from '../api/client.ts'

export type Reminder = { enabled: boolean; time: string; mutedUntil: string | null; windowMin: number; activeDevices: number }
export type MuteChoice = 'tomorrow' | '3d' | '7d' | 'disable' | 'cancel'
// Shortcuts are exclusive with enabled/mutedUntil: cancel clears the mute in one write.
export type ReminderWrite = { muteChoice: MuteChoice; enabled?: never; mutedUntil?: never } | { enabled?: boolean; time?: string; windowMin?: number; mutedUntil?: string | null; muteChoice?: never }
export type PushConfig = { publicKey: string; keyId: string }
export type Availability = { kind: 'available'; config: PushConfig } | { kind: 'unavailable'; reason: 'push_disabled' | 'push_unavailable' } | { kind: 'unknown' }
export type SubscriptionWrite = { endpoint: string; keys: { p256dh: string; auth: string }; keyId: string; replacesEndpoint?: string; reactivate?: true }
export function parseReminder(body: unknown): Reminder {
  const r = body as Reminder | null
  if (!r || typeof r.enabled !== 'boolean' || !/^([01]\d|2[0-3]):(00|15|30|45)$/.test(r.time) || (r.mutedUntil !== null && (typeof r.mutedUntil !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(r.mutedUntil))) || !Number.isInteger(r.windowMin) || r.windowMin < 15 || r.windowMin > 240 || !Number.isInteger(r.activeDevices) || r.activeDevices < 0) throw new ApiError('invalid-response')
  return r
}
export const getReminder = async () => parseReminder(await apiRequest('/api/learn/reminder'))
export async function getAvailability(): Promise<Availability> {
  try {
    const config = await apiRequest('/api/push/config') as PushConfig | null
    if (!config || typeof config.publicKey !== 'string' || typeof config.keyId !== 'string') throw new ApiError('invalid-response')
    return { kind: 'available', config }
  } catch (error) {
    if (error instanceof ApiError && (error.kind === 'unauthorized' || error.kind === 'forbidden')) throw error
    if (error instanceof ApiError && error.status === 503) return { kind: 'unavailable', reason: (error.body as { error?: string })?.error === 'push_disabled' ? 'push_disabled' : 'push_unavailable' }
    return { kind: 'unknown' }
  }
}
export function retryDeadline(value: string | null, now = Date.now()): number {
  if (value && /^\d+$/.test(value.trim())) return now + Math.max(1, Number(value)) * 1000
  const date = value ? Date.parse(value) : NaN
  return Number.isFinite(date) && date > now ? date : now + 30_000
}
export async function sendTest(endpoint: string): Promise<string> {
  const body = await apiRequest('/api/push/test', { method: 'POST', body: JSON.stringify({ endpoint }) }) as { results?: { outcome: string }[] }
  if (!Array.isArray(body?.results) || !body.results.length) return 'No hay un dispositivo disponible para la prueba. Sincroniza este dispositivo.'
  if (body.results.every(r => r.outcome === 'delivered')) return 'Prueba enviada al servicio de notificaciones. Comprueba si llega a este dispositivo.'
  return 'La prueba no se ha entregado a todos los destinos. Sincroniza este dispositivo e inténtalo de nuevo.'
}
export function windowCopy(time: string, minutes: number): string {
  const [h, m] = time.split(':').map(Number)
  const end = Math.min(h * 60 + m + minutes, 23 * 60 + 59)
  return `Solo puede sonar entre las ${time} y las ${String(Math.floor(end / 60)).padStart(2, '0')}:${String(end % 60).padStart(2, '0')}, en la zona horaria de tu cuenta. Después caduca: una notificación tardía será silenciosa.`
}
