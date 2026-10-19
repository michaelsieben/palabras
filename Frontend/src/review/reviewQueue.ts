import { ApiError } from '../api/client.ts'
import { postReviewActions } from '../api/review.ts'
import type { ApprovedWord, ReviewAction, ReviewItem, ReviewResult } from '../api/review.ts'
import { createReviewStorage, reviewDatabaseName } from './storage.ts'

export type ReviewQueuedRow = { action: ReviewAction; word: ReviewItem | ApprovedWord; state: 'pending' | 'failed' | 'applied'; result?: ReviewResult['result']; error?: string }
export interface ReviewStore { put(row: ReviewQueuedRow): Promise<void>; putMany?(rows: ReviewQueuedRow[]): Promise<void>; list(): Promise<ReviewQueuedRow[]>; remove(id: string): Promise<void> }
export function createReviewQueue(store: ReviewStore, options: { send?: typeof postReviewActions; online?: () => boolean; lock?: (work: () => Promise<void>) => Promise<void> } = {}) {
  const send = options.send ?? postReviewActions
  const online = options.online ?? (() => navigator.onLine)
  const lock = options.lock ?? (work => work())
  const listeners = new Set<() => void>()
  let snapshot: { rows: ReviewQueuedRow[]; storageError: boolean } = { rows: [], storageError: false }
  let active = false
  let writes: Promise<void> = Promise.resolve()
  let flushing: Promise<void> | undefined
  const publish = (rows: ReviewQueuedRow[], storageError = false) => { snapshot = { rows, storageError }; for (const listener of listeners) listener() }
  const refresh = async () => publish(await store.list())
  async function submit(batch: ReviewQueuedRow[]) {
    let results: ReviewResult[]
    try { results = (await send(batch.map(r => r.action))).results } catch (error) {
      // Whole transaction can roll back for corrupt content. Isolate once, preserving UUIDs.
      if (batch.length > 1 && error instanceof ApiError && error.status !== undefined && error.status >= 500) {
        for (const row of batch) { if (!active || !online()) break; await submit([row]) }
        return
      }
      const retryable = error instanceof ApiError && (error.kind === 'network' || (error.status !== undefined && error.status >= 500))
      for (const row of batch) await store.put({ ...row, state: retryable ? 'pending' : 'failed', error: error instanceof ApiError ? error.kind : 'request' })
      return
    }
    for (const row of batch) {
      const matches = results.filter(r => r.clientReviewId === row.action.clientReviewId)
      const result = matches.length === 1 ? matches[0] : undefined
      await store.put({ ...row, state: result && ['applied', 'duplicate'].includes(result.result) ? 'applied' : 'failed', result: result?.result, error: result ? undefined : 'missing-acknowledgement' })
    }
  }
  function flush(): Promise<void> {
    if (flushing) return flushing
    flushing = writes.catch(() => undefined).then(() => lock(async () => {
      const attempted = new Set<string>()
      while (active && online()) {
        const pending = (await store.list()).filter(r => r.state === 'pending' && !attempted.has(r.action.clientReviewId))
        let viaBatch = 0
        const batch = pending.filter(r => !r.action.viaBatch || ++viaBatch <= 25).slice(0, 50)
        if (!batch.length) break
        for (const row of batch) attempted.add(row.action.clientReviewId)
        await submit(batch)
        await refresh()
      }
      await refresh()
    })).catch(error => { publish(snapshot.rows, true); throw error }).finally(() => { flushing = undefined })
    return flushing
  }
  const trigger = () => { void flush().catch(() => undefined) }
  function mutate(work: () => Promise<void>) {
    const result = writes.catch(() => undefined).then(() => lock(work)).then(refresh)
    writes = result.catch(error => { publish(snapshot.rows, true); throw error })
    // Caller owns failure; keep rejected tail from becoming an unhandled rejection.
    void writes.catch(() => undefined)
    return result
  }
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener) } },
    getSnapshot: () => snapshot,
    async enqueue(action: ReviewAction, word: ReviewItem | ApprovedWord) {
      await mutate(async () => {
        const rows = await store.list()
        if (rows.some(r => r.action.clientReviewId === action.clientReviewId)) return
        if (rows.some(r => r.action.wordId === action.wordId && r.state !== 'applied')) throw new Error('Word already queued')
        await store.put({ action, word, state: 'pending' })
      })
      trigger()
    },
    async enqueueMany(entries: { action: ReviewAction; word: ReviewItem | ApprovedWord }[]) {
      await mutate(async () => {
        const existing = await store.list()
        if (entries.length > 25 || new Set(entries.map(e => e.word.wordId)).size !== entries.length || entries.some(e => existing.some(r => r.action.wordId === e.word.wordId && r.state !== 'applied'))) throw new Error('Invalid queued batch')
        const rows: ReviewQueuedRow[] = entries.map(e => ({ ...e, state: 'pending' }))
        if (store.putMany) await store.putMany(rows)
        else for (const row of rows) await store.put(row)
      })
      trigger()
    },
    async retry(id: string) {
      await mutate(async () => { const row = (await store.list()).find(r => r.action.clientReviewId === id); if (row && row.state !== 'applied') await store.put({ ...row, state: 'pending', error: undefined, result: undefined }) })
      trigger()
    },
    discard: (id: string) => mutate(() => store.remove(id)),
    flush, trigger,
    start() { active = true; trigger() },
    stop() { active = false },
  }
}
const queues = new Map<string, ReturnType<typeof createReviewQueue>>()
export function getReviewQueueStore(username: string) {
  const name = reviewDatabaseName(username)
  let queue = queues.get(name)
  if (!queue) {
    const tx = createReviewStorage(name)
    queue = createReviewQueue({ put: row => tx('actions', 'readwrite', s => { s.put(row) }), putMany: rows => tx('actions', 'readwrite', s => { for (const row of rows) s.put(row) }), list: () => tx('actions', 'readonly', s => s.getAll()), remove: id => tx('actions', 'readwrite', s => { s.delete(id) }) }, { lock: work => navigator.locks.request(name, work) })
    queues.set(name, queue)
  }
  return queue
}
