import { ApiError } from '../api/client.ts'
import { completeDay } from '../api/learn.ts'
import type { DayCompletion, DayStatus } from '../api/learn.ts'
import type { Attempt } from '../api/learn.ts'
import type { getAttemptQueue } from './attemptQueue.ts'

export type CompletionRow = { localDate: string; body: DayCompletion; state: 'pending' | 'sent' | 'rejected' }
export interface CompletionStore {
  list(): Promise<CompletionRow[]>
  put(row: CompletionRow): Promise<void>
}
export type CompletionSnapshot = { pending: number; rejected: number; error: boolean; last: DayStatus | null }

// The API exposes no zone setting yet: all current learners use its Europe/Madrid default.
// Capture at completion, never at flush (or from an old cached plan / UTC date).
export function completionLocalDate(now: Date = new Date(), timeZone = 'Europe/Madrid'): string {
  const parts = new Intl.DateTimeFormat('en', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now)
  const part = (type: string) => parts.find(item => item.type === type)!.value
  return `${part('year')}-${part('month')}-${part('day')}`
}
export function countsAsCompletedDay(stopping: boolean, attempts: readonly Pick<Attempt, 'durationMs'>[]): boolean {
  // Full composed session always counts. For an early stop use measured answer time,
  // a conservative active-time lower bound (never background wall-clock time).
  return !stopping || attempts.reduce((sum, attempt) => sum + attempt.durationMs, 0) >= 300_000
}

export function createCompletionStore(name: string, factory: IDBFactory = indexedDB): CompletionStore {
  let opening: Promise<IDBDatabase> | undefined
  function open() {
    return opening ??= new Promise((resolve, reject) => {
      const request = factory.open(name, 1)
      request.onupgradeneeded = () => request.result.createObjectStore('completions', { keyPath: 'localDate' })
      request.onerror = () => { opening = undefined; reject(request.error) }
      request.onblocked = () => { opening = undefined; reject(new Error('Completion database blocked')) }
      request.onsuccess = () => {
        const db = request.result
        db.onversionchange = () => { db.close(); opening = undefined }
        resolve(db)
      }
    })
  }
  async function transaction<T>(mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T> | void): Promise<T> {
    const db = await open()
    return new Promise((resolve, reject) => {
      const tx = db.transaction('completions', mode)
      const request = work(tx.objectStore('completions'))
      tx.oncomplete = () => resolve(request?.result as T)
      tx.onerror = tx.onabort = () => reject(tx.error ?? new Error('Completion transaction failed'))
    })
  }
  return { list: () => transaction('readonly', store => store.getAll()), put: row => transaction('readwrite', store => { store.put(row) }) }
}

export function createCompletionQueue(store: CompletionStore, attempts: Pick<ReturnType<typeof getAttemptQueue>, 'flush' | 'getSnapshot'>, options: {
  send?: typeof completeDay; online?: () => boolean; lock?: (work: () => Promise<void>) => Promise<void>
} = {}) {
  const send = options.send ?? completeDay
  const online = options.online ?? (() => navigator.onLine)
  const lock = options.lock ?? (work => work())
  const listeners = new Set<() => void>()
  let snapshot: CompletionSnapshot = { pending: 0, rejected: 0, error: false, last: null }
  let active = false
  let writes: Promise<void> = Promise.resolve()
  let flushing: Promise<void> | undefined
  let requested = false
  async function refresh(error = false, last = snapshot.last) {
    const rows = await store.list()
    snapshot = { pending: rows.filter(row => row.state === 'pending').length, rejected: rows.filter(row => row.state === 'rejected').length, error, last }
    for (const listener of listeners) listener()
  }
  function flush(): Promise<void> {
    if (flushing) return flushing
    // Await enqueues outside the lock: their own read/put is protected by the same Web Lock.
    flushing = writes.catch(() => undefined).then(() => lock(async () => {
      if (!active || !online()) { await refresh(); return }
      await attempts.flush()
      if (attempts.getSnapshot().pending || attempts.getSnapshot().error) throw new Error('Answers pending')
      for (const row of (await store.list()).filter(row => row.state === 'pending').sort((a, b) => a.localDate.localeCompare(b.localDate))) {
        if (!active || !online()) break
        try {
          const day = await send(row.body)
          // Durable tombstone prevents replay after reload or another completed round that day.
          await store.put({ ...row, state: 'sent' })
          await refresh(false, day)
        } catch (error) {
          if (!(error instanceof ApiError && error.status === 400)) throw error
          // Never retry an expired date as today; preserve rejection for visible explanation.
          await store.put({ ...row, state: 'rejected' })
          await refresh()
        }
      }
      await refresh()
    })).catch(async error => { requested = false; await refresh(true); throw error }).finally(() => {
      flushing = undefined
      if (requested) { requested = false; trigger() }
    })
    return flushing
  }
  const trigger = () => {
    if (flushing) { requested = true; return }
    void flush().catch(() => undefined)
  }
  function enqueue(body: DayCompletion): Promise<void> {
    const job = writes.catch(() => undefined).then(() => lock(async () => {
      if (!(await store.list()).some(row => row.localDate === body.localDate)) await store.put({ localDate: body.localDate, body, state: 'pending' })
      await refresh()
    }))
    writes = job
    void job.then(trigger, () => undefined)
    return job
  }
  return {
    enqueue, flush, trigger,
    getSnapshot: () => snapshot,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener) } },
    start() { active = true; trigger() }, stop() { active = false },
  }
}

const queues = new Map<string, ReturnType<typeof createCompletionQueue>>()
export function getCompletionQueue(username: string, attempts: ReturnType<typeof getAttemptQueue>) {
  const name = `palabras-completions:${encodeURIComponent(username)}`
  let queue = queues.get(name)
  if (!queue) {
    queue = createCompletionQueue(createCompletionStore(name), attempts, { lock: work => navigator.locks.request(name, work) })
    queues.set(name, queue)
  }
  return queue
}
