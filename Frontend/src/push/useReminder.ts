import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { getAvailability, getReminder } from './api'
import type { Availability, Reminder } from './api'
import { getPushQueue } from './outbox'
import { createPushClient } from './pushClient'
import type { PermissionState } from './pushClient'
import { getDayStatus } from '../api/learn'

const emptySnapshot = { pending: 0, rejected: 0, error: false, storageError: false, revision: 0, lastState: '', reason: '' }
const noopSubscribe = () => () => {}
const getEmpty = () => emptySnapshot
export function useReminder(username: string | null) {
  const queue = useMemo(() => username ? getPushQueue(username) : null, [username])
  const client = useMemo(() => queue ? createPushClient(queue.enqueue) : null, [queue])
  const generation = useRef({ value: 0 })
  useEffect(() => { const token = generation.current; token.value++; return () => { token.value++ } }, [client])
  const outbox = useSyncExternalStore(queue?.subscribe ?? noopSubscribe, queue?.getSnapshot ?? getEmpty)
  const [data, setData] = useState<{ username: string | null; reminder: Reminder | null; availability: Availability; device: { state: PermissionState; endpoint: string | null }; error: string }>({ username: null, reminder: null, availability: { kind: 'unknown' }, device: { state: 'unsupported', endpoint: null }, error: '' })
  const refresh = useCallback(async (heal = false) => {
    if (!client) return
    const current = generation.current.value
    try {
      const [reminder, availability] = await Promise.all([getReminder(), getAvailability()])
      if (current !== generation.current.value) return
      const device = heal && reminder.enabled ? await client.sync(availability) : await client.inspect()
      if (current === generation.current.value) setData({ username, reminder, availability, device, error: '' })
    } catch {
      // Never retain an old available/0 pair after a failed refresh.
      if (current === generation.current.value) setData(previous => ({ ...previous, username, availability: { kind: 'unknown' }, error: 'No se han podido comprobar los recordatorios. Comprueba la conexión y reintenta.' }))
    }
  }, [client, username])
  useEffect(() => {
    if (!queue || !client) return
    let active = true
    const unsubscribe = queue.subscribe(() => { if (active && !queue.getSnapshot().pending) void refresh() })
    queue.start()
    const opened = () => { if (active) { queue.trigger(); void refresh(true) } }
    const visible = () => { if (document.visibilityState === 'visible') opened() }
    opened()
    window.addEventListener('online', opened)
    document.addEventListener('visibilitychange', visible)
    navigator.serviceWorker?.addEventListener('controllerchange', opened)
    // A closed day should dismiss a stale notification on this device, not infer delivery.
    void getDayStatus().then(async day => {
      if (!active || !['done', 'skipped'].includes(day.status ?? '')) return
      const registration = await navigator.serviceWorker?.getRegistration('/')
      for (const notification of await registration?.getNotifications({ tag: 'daily' }) ?? []) notification.close()
    }).catch(() => undefined)
    return () => { active = false; unsubscribe(); queue.stop(); window.removeEventListener('online', opened); document.removeEventListener('visibilitychange', visible); navigator.serviceWorker?.removeEventListener('controllerchange', opened) }
  }, [queue, client, refresh])
  return { ...(data.username === username ? data : { reminder: null, availability: { kind: 'unknown' } as Availability, device: { state: 'unsupported' as PermissionState, endpoint: null }, error: '' }), client, queue, outbox, refresh }
}
export type ReminderModel = ReturnType<typeof useReminder>
