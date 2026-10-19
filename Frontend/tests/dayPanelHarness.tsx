import { createRoot } from 'react-dom/client'
import { DayStatusPanel } from '../src/learn/DayStatusPanel'
import type { CompletionSnapshot, getCompletionQueue } from '../src/learn/completionQueue'

let snapshot: CompletionSnapshot = { pending: 0, rejected: 0, error: false, last: null }
const listeners = new Set<() => void>()
function publish(next: CompletionSnapshot) {
  snapshot = next
  listeners.forEach(listener => listener())
}
const queue: ReturnType<typeof getCompletionQueue> = {
  getSnapshot: () => snapshot,
  subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener) } },
  enqueue: async () => {}, flush: async () => {}, trigger() {}, start() {}, stop() {},
}
const last = { localDate: '2026-10-25', status: 'done' as const, streak: 1, skipAvailable: true, skipNextAvailable: null }
createRoot(document.getElementById('root')!).render(<>
  <DayStatusPanel completionQueue={queue} />
  <button onClick={() => publish({ ...snapshot, last: { ...last } })}>Publish completion</button>
  <button onClick={() => publish({ ...snapshot, pending: snapshot.pending + 1, last: snapshot.last && { ...snapshot.last } })}>Enqueue unchanged completion</button>
  <button onClick={() => publish({ ...snapshot, rejected: snapshot.rejected + 1, error: true, last: snapshot.last && { ...snapshot.last } })}>Reject unchanged completion</button>
  <button onClick={() => publish({ ...snapshot, last: { ...last, streak: 2 } })}>Publish changed completion</button>
</>)
