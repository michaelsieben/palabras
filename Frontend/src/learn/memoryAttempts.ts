import { postAttempts } from '../api/learn.ts'
import type { Attempt, AttemptResult } from '../api/learn.ts'

// S10 replacement boundary: enqueue owns transport/persistence, never the reducer.
// S9 sends each answer immediately; failures remain in memory for an explicit retry.
export type EnqueueAttempt = (attempt: Attempt) => Promise<AttemptResult>
export function createMemoryEnqueue(): EnqueueAttempt {
  const pending = new Map<string, Attempt>()
  const sending = new Map<string, Promise<AttemptResult>>()
  const acknowledged = new Map<string, AttemptResult>()
  let tail: Promise<unknown> = Promise.resolve()
  return attempt => {
    const id = attempt.clientAttemptId
    const done = acknowledged.get(id)
    if (done) return Promise.resolve(done)
    const active = sending.get(id)
    if (active) return active
    pending.set(id, attempt)
    const work = tail.catch(() => undefined).then(async () => {
      const body = await postAttempts([pending.get(id)!])
      const result = body.results.find(item => item.clientAttemptId === id)
      if (!result) throw new Error('Missing attempt acknowledgement')
      acknowledged.set(id, result)
      pending.delete(id)
      return result
    }).finally(() => { sending.delete(id) })
    sending.set(id, work)
    tail = work
    return work
  }
}
