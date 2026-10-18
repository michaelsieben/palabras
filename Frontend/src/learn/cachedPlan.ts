import { ApiError } from '../api/client.ts'
import { getSessionPlan, parseSessionPlan } from '../api/learn.ts'
import type { SessionPlan } from '../api/learn.ts'

export async function loadCachedPlan(username: string, signal?: AbortSignal): Promise<{ plan: SessionPlan; offline: boolean }> {
  const key = `palabras-plan:${encodeURIComponent(username)}`
  try {
    const plan = await getSessionPlan({}, signal)
    // Cache failure must not prevent an online session.
    try { localStorage.setItem(key, JSON.stringify(plan)) } catch { /* storage unavailable */ }
    return { plan, offline: false }
  } catch (error) {
    // Never use cached learner content to bypass expired auth or denied access.
    if (signal?.aborted || !(error instanceof ApiError) || ['unauthorized', 'forbidden'].includes(error.kind)) throw error
    try {
      const cached = localStorage.getItem(key)
      if (cached) return { plan: parseSessionPlan(JSON.parse(cached)), offline: true }
    } catch { /* absent, corrupt or inaccessible cache: original failure */ }
    throw error
  }
}
