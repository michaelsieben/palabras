import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { getAvailability, getDevices, getReminder } from './api'
import type { Availability, DeviceList, PushDevice, Reminder } from './api'
import { getPushQueue } from './outbox'
import { createPushClient } from './pushClient'
import type { PermissionState } from './pushClient'
import { getDayStatus } from '../api/learn'

const emptySnapshot = { pending: 0, rejected: 0, error: false, storageError: false, revision: 0, lastState: '', reason: '', currentId: null, unavailable: '' }
const noopSubscribe = () => () => {}
const getEmpty = () => emptySnapshot
export function useReminder(username: string | null) {
  const queue = useMemo(() => username ? getPushQueue(username) : null, [username])
  const client = useMemo(() => queue ? createPushClient(queue.enqueue) : null, [queue])
  const generation = useRef({ value: 0 })
  const request = useRef(0)
  useEffect(() => { const token = generation.current; token.value++; return () => { token.value++ } }, [client])
  const outbox = useSyncExternalStore(queue?.subscribe ?? noopSubscribe, queue?.getSnapshot ?? getEmpty)
  const [data, setData] = useState<{ username: string | null; reminder: Reminder | null; availability: Availability; device: { state: PermissionState; endpoint: string | null }; devices: DeviceList | null; devicesError: string; revision: number; error: string }>({ username: null, reminder: null, availability: { kind: 'unknown' }, device: { state: 'unsupported', endpoint: null }, devices: null, devicesError: '', revision: 0, error: '' })
  const refresh = useCallback(async (heal = false) => {
    if (!client) return
    const current = generation.current.value
    const sequence = ++request.current
    const revision = queue?.getSnapshot().revision ?? 0
    try {
      const [reminder, availability] = await Promise.all([getReminder(), getAvailability()])
      // A queue-start inspection may supersede this render, but must not cancel
      // app-open healing. Only an account change cancels the lifecycle work.
      if (current !== generation.current.value) return
      const device = heal && reminder.enabled ? await client.sync(availability) : await client.inspect()
      let devices: DeviceList | null = null
      let devicesError = ''
      try { devices = await getDevices(queue?.getSnapshot().currentId ?? null) }
      catch { devicesError = 'No se ha podido leer la lista de dispositivos. Comprueba la conexión y vuelve a comprobar.' }
      if (current === generation.current.value && sequence === request.current) setData({ username, reminder, availability, device, devices, devicesError, revision, error: '' })
    } catch {
      // Never retain an old available/0 pair after a failed refresh.
      if (current === generation.current.value && sequence === request.current) setData(previous => ({ ...previous, username, availability: { kind: 'unknown' }, error: 'No se han podido comprobar los recordatorios. Comprueba la conexión y reintenta.' }))
    }
  }, [client, queue, username])
  const revoke = useCallback(async (row: PushDevice) => {
    if (!client || !queue) throw new Error('La sesión ya no está disponible.')
    if (row.isCurrent) await client.optOut(row.id)
    else await queue.enqueue({ kind: 'revoke', body: { id: row.id, current: false } })
  }, [client, queue])
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
  const unavailable: Availability | null = outbox.unavailable && outbox.revision > data.revision ? { kind: 'unavailable', reason: outbox.unavailable === 'push_disabled' ? 'push_disabled' : 'push_unavailable' } : null
  return { ...(data.username === username ? { ...data, availability: unavailable ?? data.availability, device: outbox.lastState === 'revoked' && !['unsupported', 'denied'].includes(data.device.state) ? { ...data.device, state: 'opted-out' as PermissionState } : data.device } : { reminder: null, availability: { kind: 'unknown' } as Availability, device: { state: 'unsupported' as PermissionState, endpoint: null }, devices: null, devicesError: '', error: '' }), client, queue, outbox, refresh, revoke }
}
export type ReminderModel = ReturnType<typeof useReminder>
