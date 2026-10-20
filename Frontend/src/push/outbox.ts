import { ApiError, apiRequest } from '../api/client.ts'
import type { ReminderWrite, SubscriptionWrite } from './api.ts'
import { createPushStore, deviceLock, readOptOut, writeOptOut } from './storage.ts'

export type Mutation = { kind: 'reminder'; body: ReminderWrite } | { kind: 'subscribe'; body: SubscriptionWrite } | { kind: 'unsubscribe'; body: { endpoint: string } } | { kind: 'revoke'; body: { id: number; current: boolean } }
export type PushRow = Mutation & { id: string; order: number; state: 'pending' | 'sent' | 'rejected'; reason?: string; subscriptionId?: number }
export interface PushStore { list(): Promise<PushRow[]>; put(row: PushRow): Promise<void>; remove(id: string): Promise<void> }
export async function sendMutation(row: Mutation) {
  const send = () => row.kind === 'revoke'
    ? apiRequest(`/api/push/subscriptions/${row.body.id}`, { method: 'DELETE' })
    : apiRequest(row.kind === 'reminder' ? '/api/learn/reminder' : '/api/push/subscription', { method: row.kind === 'unsubscribe' ? 'DELETE' : 'PUT', body: JSON.stringify(row.body) })
  if (row.kind !== 'subscribe') return send()
  return navigator.locks.request(deviceLock, async () => {
    if (await readOptOut()) throw new ApiError('http', 409, { error: 'device_opted_out' })
    try { return await send() }
    catch (error) {
      // A server opt-out is not an app-open failure and must block passive healing.
      if (error instanceof ApiError && error.status === 409 && (error.body as { error?: string })?.error === 'revoked') await writeOptOut(true)
      throw error
    }
  })
}
export function createPushQueue(store: PushStore, options: { send?: typeof sendMutation; online?: () => boolean; lock?: (work: () => Promise<void>) => Promise<void> } = {}) {
  const send = options.send ?? sendMutation
  const lock = options.lock ?? (work => work())
  const online = options.online ?? (() => navigator.onLine)
  const listeners = new Set<() => void>()
  let snapshot = { pending: 0, rejected: 0, error: false, storageError: false, revision: 0, lastState: '', reason: '', currentId: null as number | null, unavailable: '' }
  let active = false
  let writes = Promise.resolve()
  let flushing: Promise<void> | undefined
  let requested = false
  async function refresh(error = false, lastState = snapshot.lastState) {
    try {
      const rows = (await store.list()).sort((a, b) => a.order - b.order)
      const rejected = rows.filter(r => r.state === 'rejected')
      const optedOut = (r: PushRow) => r.kind === 'subscribe' && ['revoked', 'device_opted_out'].includes(r.reason ?? '')
      const unavailable = (r: PushRow) => ['push_disabled', 'push_unavailable'].includes(r.reason ?? '')
      snapshot = {
        pending: rows.filter(r => r.state === 'pending').length,
        rejected: rejected.filter(r => !optedOut(r) && !unavailable(r)).length,
        error, storageError: false, revision: snapshot.revision + 1,
        lastState: rejected.some(optedOut) ? 'revoked' : lastState,
        reason: rejected.at(-1)?.reason ?? '',
        currentId: rows.filter(r => r.kind === 'subscribe' && r.state === 'sent' && r.subscriptionId).at(-1)?.subscriptionId ?? null,
        unavailable: rejected.find(unavailable)?.reason ?? '',
      }
    } catch { snapshot = { ...snapshot, error: true, storageError: true, revision: snapshot.revision + 1 } }
    listeners.forEach(l => l())
  }
  function flush(): Promise<void> {
    if (flushing) return flushing
    flushing = writes.catch(() => undefined).then(() => lock(async () => {
      if (!active || !online()) { await refresh(); return }
      for (const row of (await store.list()).filter(r => r.state === 'pending').sort((a, b) => a.order - b.order)) {
        if (!active || !online()) break
        try {
          const result = await send(row) as { id?: number; state?: string } | undefined
          const subscriptionId = result?.id
          if (row.kind === 'subscribe' && (typeof subscriptionId !== 'number' || !Number.isSafeInteger(subscriptionId) || subscriptionId <= 0)) throw new ApiError('invalid-response')
          await store.put({ ...row, state: 'sent', ...(row.kind === 'subscribe' ? { subscriptionId } : {}) })
          await refresh(false, row.kind === 'unsubscribe' || (row.kind === 'revoke' && row.body.current) ? 'revoked' : result?.state ?? snapshot.lastState)
        } catch (error) {
          if (!(error instanceof ApiError && ([400, 404, 409].includes(error.status ?? 0) || (error.status === 503 && row.kind !== 'reminder')))) throw error
          await store.put({ ...row, state: 'rejected', reason: (error.body as { error?: string })?.error ?? 'invalid' })
          await refresh()
        }
      }
      await refresh()
    })).catch(async () => { requested = false; await refresh(true) }).finally(() => {
      flushing = undefined
      if (requested) { requested = false; trigger() }
    })
    return flushing
  }
  function trigger() { if (flushing) { requested = true; return }; void flush() }
  function enqueue(mutation: Mutation) {
    const job = writes.catch(() => undefined).then(() => lock(async () => {
      // Coalesce passive resync; never replay a queued activation after a device opt-out.
      const rows = await store.list()
      // Keep the latest acknowledged PUT id durably, including across reload/opt-out.
      const identity = rows.filter(r => r.kind === 'subscribe' && r.state === 'sent' && r.subscriptionId).sort((a, b) => b.order - a.order)[0]
      for (const row of rows) {
        if ((row.state === 'sent' && row.id !== identity?.id) || row.state === 'rejected' || (row.state === 'pending' && row.kind === 'subscribe' && (mutation.kind === 'subscribe' || mutation.kind === 'unsubscribe' || (mutation.kind === 'revoke' && mutation.body.current)))) await store.remove(row.id)
      }
      await store.put({ ...mutation, id: crypto.randomUUID(), order: Math.max(0, ...rows.map(r => r.order)) + 1, state: 'pending' })
      await refresh()
    }))
    writes = job
    void job.then(trigger, () => undefined)
    return job
  }
  return { enqueue, flush, trigger, getSnapshot: () => snapshot, subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l) } }, start() { active = true; trigger() }, stop() { active = false } }
}
const queues = new Map<string, ReturnType<typeof createPushQueue>>()
export function getPushQueue(username: string) {
  const name = `palabras-push:${encodeURIComponent(username)}`
  let queue = queues.get(name)
  if (!queue) { queue = createPushQueue(createPushStore<PushRow>(name), { lock: work => navigator.locks.request(name, work) }); queues.set(name, queue) }
  return queue
}
