import type { Availability, SubscriptionWrite } from './api.ts'
import type { Mutation } from './outbox.ts'
import { deviceLock, readOptOut, writeOptOut } from './storage.ts'

export type PermissionState = 'unsupported' | 'default' | 'denied' | 'granted-unsubscribed' | 'subscribed' | 'opted-out'
export interface PushEnvironment {
  supported(): boolean; permission(): NotificationPermission; requestPermission(): Promise<NotificationPermission>
  registration(): Promise<Pick<ServiceWorkerRegistration, 'pushManager'>>
  optedOut(): Promise<boolean>; setOptOut(value: boolean): Promise<void>
  lock<T>(work: () => Promise<T>): Promise<T>
}
export const browserPush: PushEnvironment = {
  supported: () => window.isSecureContext && 'Notification' in window && 'PushManager' in window && 'serviceWorker' in navigator && 'indexedDB' in window && !!navigator.locks,
  permission: () => Notification.permission,
  requestPermission: () => Notification.requestPermission(),
  // Never wait forever for ready: development and failed SW installs must remain usable.
  registration: async () => { const r = await navigator.serviceWorker.getRegistration('/'); if (!r?.active) throw new Error('El servicio de notificaciones aún no está listo. Reintenta cuando la app termine de prepararse.'); return r },
  optedOut: readOptOut, setOptOut: writeOptOut,
  lock: work => navigator.locks.request(deviceLock, work),
}
export function permissionState(supported: boolean, permission: NotificationPermission, subscribed: boolean, optedOut: boolean): PermissionState {
  return !supported ? 'unsupported' : permission === 'denied' ? 'denied' : optedOut ? 'opted-out' : permission === 'default' ? 'default' : subscribed ? 'subscribed' : 'granted-unsubscribed'
}
export const permissionCopy: Record<PermissionState, string> = {
  unsupported: 'Recordatorios no disponibles en este navegador. Usa Chrome con HTTPS.',
  default: 'Puedes activar las notificaciones en este dispositivo. Solo pediremos permiso al pulsar el botón.',
  denied: 'Chrome bloquea las notificaciones. No volveremos a pedir permiso. En Chrome: menú ⋮ → Información del sitio → Notificaciones → Permitir. Si usas la app instalada: Ajustes de Android → Apps → Palabras → Notificaciones.',
  'granted-unsubscribed': 'Permiso concedido, pero este dispositivo aún no está suscrito. Actívalo o sincronízalo para recibir recordatorios.',
  subscribed: 'Este navegador tiene una suscripción. La disponibilidad del servidor se comprueba por separado.',
  'opted-out': 'Has desactivado los recordatorios en este dispositivo. No se reactivarán al abrir la app.',
}
function keyBytes(publicKey: string) {
  const base64 = publicKey.replaceAll('-', '+').replaceAll('_', '/')
  return Uint8Array.from(atob(base64 + '='.repeat((4 - base64.length % 4) % 4)), c => c.charCodeAt(0))
}
export function createPushClient(enqueue: (mutation: Mutation) => Promise<void>, env = browserPush) {
  async function inspect() {
    if (!env.supported()) return { state: 'unsupported' as PermissionState, endpoint: null }
    const optedOut = await env.optedOut()
    let subscription: PushSubscription | null = null
    try { subscription = await (await env.registration()).pushManager.getSubscription() } catch { /* Show permission honestly even before worker activation. */ }
    return { state: permissionState(true, env.permission(), !!subscription, optedOut), endpoint: subscription?.endpoint ?? null }
  }
  async function sync(availability: Availability, explicit = false) {
    // Prompt directly in the click task, before locks/network/storage awaits.
    if (!env.supported() || availability.kind !== 'available') return inspect()
    if (explicit && env.permission() === 'default') await env.requestPermission()
    let mutation: Mutation | undefined
    let cleanup: PushSubscription | undefined
    const result = await env.lock(async () => {
      if (!explicit && await env.optedOut()) return inspect()
      let registration: Pick<ServiceWorkerRegistration, 'pushManager'>
      try { registration = await env.registration() }
      catch (error) { if (env.permission() !== 'granted') return inspect(); throw error }
      let subscription = await registration.pushManager.getSubscription()
      if (env.permission() !== 'granted') {
        if (subscription) { mutation = { kind: 'unsubscribe', body: { endpoint: subscription.endpoint } }; cleanup = subscription }
        return inspect()
      }
      if (explicit) await env.setOptOut(false)
      const previous = subscription?.endpoint
      const key = keyBytes(availability.config.publicKey)
      const oldKey = subscription?.options.applicationServerKey
      if (subscription && (!oldKey || new Uint8Array(oldKey).length !== key.length || !new Uint8Array(oldKey).every((v, i) => v === key[i]))) { await subscription.unsubscribe(); subscription = null }
      subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
      const serialized = subscription.toJSON()
      if (!serialized.endpoint || !serialized.keys?.p256dh || !serialized.keys.auth) throw new Error('La suscripción no es válida. Reintenta la activación.')
      const body: SubscriptionWrite = { endpoint: serialized.endpoint, keys: { p256dh: serialized.keys.p256dh, auth: serialized.keys.auth }, keyId: availability.config.keyId }
      if (previous && previous !== serialized.endpoint) body.replacesEndpoint = previous
      if (explicit) body.reactivate = true
      mutation = { kind: 'subscribe', body }
      return inspect()
    })
    // Do not acquire the outbox lock while holding the device lock: a flusher
    // holds the account lock before checking the device opt-out under its lock.
    if (mutation) await enqueue(mutation)
    if (cleanup) { await cleanup.unsubscribe(); return inspect() }
    return result
  }
  async function optOut() {
    let mutation: Mutation | undefined
    let cleanup: PushSubscription | undefined
    const result = await env.lock(async () => {
      // Worker reads this same durable flag, under the same cross-context Web Lock.
      await env.setOptOut(true)
      const subscription = await (await env.registration()).pushManager.getSubscription()
      if (subscription) { mutation = { kind: 'unsubscribe', body: { endpoint: subscription.endpoint } }; cleanup = subscription }
      return inspect()
    })
    if (mutation) await enqueue(mutation)
    // Do not lose the only known endpoint until the deletion is durably queued.
    if (cleanup) await cleanup.unsubscribe()
    return result
  }
  return { inspect, sync, optOut }
}
