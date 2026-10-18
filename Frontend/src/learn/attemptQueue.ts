import { postAttempts } from '../api/learn.ts'
import type { Attempt, AttemptResult } from '../api/learn.ts'
import type { EnqueueAttempt } from './memoryAttempts.ts'

export interface AttemptStore {
  put(attempt: Attempt): Promise<void>
  list(): Promise<Attempt[]>
  remove(ids: string[]): Promise<void>
}
export type QueueSnapshot = { pending: number; error: boolean }
const terminal = new Set(['applied', 'duplicate', 'stale_epoch', 'suspended', 'out_of_order'])

// One store, account-scoped database; no identity is added to the API payload.
export function createIndexedDbStore(name: string, factory: IDBFactory = indexedDB): AttemptStore {
  let opening: Promise<IDBDatabase> | undefined
  function open() {
    return opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name, 1)
      request.onupgradeneeded = () => request.result.createObjectStore('attempts', { keyPath: 'clientAttemptId' })
      request.onerror = () => { opening = undefined; reject(request.error) }
      request.onblocked = () => { opening = undefined; reject(new Error('Attempt database blocked')) }
      request.onsuccess = () => {
        const db = request.result
        db.onversionchange = () => { db.close(); opening = undefined }
        resolve(db)
      }
    })
  }
  async function transaction<T>(mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T> | void): Promise<T> {
    const db = await open()
    return new Promise<T>((resolve, reject) => {
      const tx = db.transaction('attempts', mode)
      const request = work(tx.objectStore('attempts'))
      tx.oncomplete = () => resolve(request?.result as T)
      tx.onerror = tx.onabort = () => reject(tx.error ?? new Error('Attempt transaction failed'))
    })
  }
  return {
    put: attempt => transaction('readwrite', store => { store.put(attempt) }),
    list: () => transaction('readonly', store => store.getAll()),
    remove: ids => transaction('readwrite', store => { for (const id of ids) store.delete(id) }),
  }
}

export function createAttemptQueue(store: AttemptStore, options: {
  send?: typeof postAttempts
  online?: () => boolean
  lock?: (work: () => Promise<void>) => Promise<void>
} = {}) {
  const send = options.send ?? postAttempts
  const online = options.online ?? (() => navigator.onLine)
  const lock = options.lock ?? (work => work())
  const listeners = new Set<() => void>()
  const waiters = new Map<string, { resolve(result: AttemptResult): void; reject(error: unknown): void; promise: Promise<AttemptResult> }>()
  const acknowledged = new Map<string, AttemptResult>()
  let snapshot: QueueSnapshot = { pending: 0, error: false }
  let writes: Promise<unknown> = Promise.resolve()
  let flushing: Promise<void> | undefined
  let active = false
  function publish(pending: number, error = false) {
    snapshot = { pending, error }
    for (const listener of listeners) listener()
  }
  async function refresh(error = false) { publish((await store.list()).length, error) }
  function flush(): Promise<void> {
    if (flushing) return flushing
    flushing = lock(async () => {
      await writes.catch(() => undefined)
      if (!active || !online()) { await refresh(); return }
      while (active && online()) {
        // Include answers committed while an earlier batch was in flight.
        await writes.catch(() => undefined)
        const batch = (await store.list()).sort((a, b) => a.reviewedAtUtc.localeCompare(b.reviewedAtUtc)
          || a.clientAttemptId.localeCompare(b.clientAttemptId)).slice(0, 50)
        if (!batch.length) break
        const response = await send(batch)
        const ids = new Set(batch.map(item => item.clientAttemptId))
        const results = response.results.filter(item => ids.has(item.clientAttemptId) && terminal.has(item.status))
        await store.remove(results.map(item => item.clientAttemptId))
        for (const result of results) {
          acknowledged.set(result.clientAttemptId, result)
          waiters.get(result.clientAttemptId)?.resolve(result)
          waiters.delete(result.clientAttemptId)
        }
        await refresh()
        // Missing acknowledgements stay durable, but never spin/retry in this trigger.
        if (new Set(results.map(item => item.clientAttemptId)).size !== batch.length) throw new Error('Missing attempt acknowledgement')
      }
      await refresh()
    }).catch(async error => { await refresh(true); throw error }).finally(() => { flushing = undefined })
    return flushing
  }
  const trigger = () => { void flush().catch(() => undefined) }
  const enqueue: EnqueueAttempt = attempt => {
    const id = attempt.clientAttemptId
    const done = acknowledged.get(id)
    if (done) return Promise.resolve(done)
    const waiting = waiters.get(id)
    if (waiting) return waiting.promise
    let resolve!: (result: AttemptResult) => void
    let reject!: (error: unknown) => void
    const promise = new Promise<AttemptResult>((yes, no) => { resolve = yes; reject = no })
    waiters.set(id, { resolve, reject, promise })
    writes = writes.catch(() => undefined).then(async () => {
      await store.put(attempt)
      await refresh()
      if (snapshot.pending >= 10) trigger()
    }).catch(error => { waiters.delete(id); reject(error) })
    return promise
  }
  return {
    enqueue, flush, trigger,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener) } },
    getSnapshot: () => snapshot,
    start() { active = true; trigger() },
    stop() { active = false },
  }
}

const queues = new Map<string, ReturnType<typeof createAttemptQueue>>()
export function getAttemptQueue(username: string) {
  const name = `palabras-attempts:${encodeURIComponent(username)}`
  let queue = queues.get(name)
  if (!queue) {
    // Web Locks holds across read/send/delete, including other tabs. Android Chrome supports it.
    queue = createAttemptQueue(createIndexedDbStore(name), {
      lock: work => navigator.locks.request(name, work),
    })
    queues.set(name, queue)
  }
  return queue
}
