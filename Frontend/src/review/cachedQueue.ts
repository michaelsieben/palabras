import { ApiError } from '../api/client.ts'
import { getReviewQueue, parseReviewQueue } from '../api/review.ts'
import type { ReviewQueueResponse } from '../api/review.ts'
import { createReviewStorage, reviewDatabaseName } from './storage.ts'

export function createCachedQueue(storage: { read(): Promise<unknown>; write(data: ReviewQueueResponse): Promise<void> }, fetchQueue = getReviewQueue) {
  return {
    write: storage.write,
    async load(signal?: AbortSignal): Promise<{ data: ReviewQueueResponse; offline: boolean; cacheFailed: boolean }> {
      try {
        const data = await fetchQueue(signal)
        let cacheFailed = false
        try { await storage.write(data) } catch { cacheFailed = true }
        return { data, offline: false, cacheFailed }
      } catch (error) {
        if (signal?.aborted || !(error instanceof ApiError) || error.kind !== 'network') throw error
        try { return { data: parseReviewQueue(await storage.read()), offline: true, cacheFailed: false } } catch { throw error }
      }
    },
  }
}
const caches = new Map<string, ReturnType<typeof createCachedQueue>>()
export function getCachedQueue(username: string) {
  const name = reviewDatabaseName(username)
  let cache = caches.get(name)
  if (!cache) {
    const tx = createReviewStorage(name)
    cache = createCachedQueue({ read: () => tx('cache', 'readonly', s => s.get('queue')), write: data => tx('cache', 'readwrite', s => { s.put(data, 'queue') }) })
    caches.set(name, cache)
  }
  return cache
}
