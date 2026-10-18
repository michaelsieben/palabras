import test from 'node:test'
import assert from 'node:assert/strict'
import { createAttemptQueue } from '../src/learn/attemptQueue.ts'

const attempt = n => ({ clientAttemptId: `attempt-${n}`, cardId: n, cardEpoch: 3, sessionId: 'session',
  mode: 'typed', rating: 3, isCorrectMeaning: true, isCorrectForm: false, overridden: false,
  feedbackKind: 'near-miss-accent', answerText: 'cafe', durationMs: 1234, reviewedAtUtc: '2026-10-18T12:00:00Z' })
function store() {
  const rows = new Map()
  return { put: async a => { rows.set(a.clientAttemptId, structuredClone(a)) },
    list: async () => structuredClone([...rows.values()]), remove: async ids => { for (const id of ids) rows.delete(id) } }
}
const applied = batch => ({ results: batch.map(a => ({ clientAttemptId: a.clientAttemptId, status: 'applied' })) })
const tick = () => new Promise(resolve => setImmediate(resolve))

test('persists entire attempt, flushes at ten, coalesces racing end/online triggers without duplicate sends', async () => {
  const storage = store(); const batches = []
  let release
  const gate = new Promise(resolve => { release = resolve })
  const queue = createAttemptQueue(storage, { online: () => true, send: async batch => { batches.push(batch); await gate; return applied(batch) } })
  queue.start(); await queue.flush()
  const promises = Array.from({ length: 10 }, (_, i) => queue.enqueue(attempt(i)))
  assert.equal(queue.enqueue(attempt(0)), promises[0])
  await tick()
  assert.equal(batches.length, 1)
  assert.equal(batches[0].length, 10)
  assert.deepEqual(batches[0].find(a => a.cardId === 0), attempt(0))
  const end = queue.flush(); const online = queue.flush()
  assert.equal(end, online)
  release(); await Promise.all([...promises, end, online])
  assert.equal(batches.length, 1)
  assert.deepEqual(await storage.list(), [])
  await queue.enqueue(attempt(0)); await queue.flush()
  assert.equal(batches.length, 1)
})

test('failed batch survives queue recreation; drains <=50 with every terminal status cleared', async () => {
  const storage = store()
  for (let i = 0; i < 121; i++) await storage.put(attempt(i))
  let connected = false
  const failing = createAttemptQueue(storage, { online: () => connected, send: async () => { throw new Error('network') } })
  failing.start(); await failing.flush(); connected = true
  await assert.rejects(failing.flush(), /network/)
  assert.equal((await storage.list()).length, 121)
  assert.equal(failing.getSnapshot().error, true)
  failing.stop()
  const batches = []
  const statuses = ['applied', 'duplicate', 'stale_epoch', 'suspended', 'out_of_order']
  const reloaded = createAttemptQueue(storage, { online: () => true, send: async batch => {
    batches.push(batch)
    return { results: batch.map((a, i) => ({ clientAttemptId: a.clientAttemptId, status: statuses[i % 5] })) }
  } })
  reloaded.start(); await reloaded.flush()
  assert.deepEqual(batches.map(b => b.length), [50, 50, 21])
  assert.equal(new Set(batches.flat().map(a => a.clientAttemptId)).size, 121)
  assert.deepEqual(await storage.list(), [])
})

test('offline retains fewer than ten; end flush and reconnect retain identity; missing ack cannot spin', async () => {
  const storage = store(); let connected = false; const sent = []
  const queue = createAttemptQueue(storage, { online: () => connected, send: async batch => { sent.push(batch); return applied(batch) } })
  queue.start(); await queue.flush()
  const pending = queue.enqueue(attempt(1)); await tick(); await queue.flush()
  assert.equal(sent.length, 0); assert.equal(queue.getSnapshot().pending, 1)
  connected = true; await queue.flush(); await pending
  assert.deepEqual(sent, [[attempt(1)]])
  const partial = createAttemptQueue(storage, { online: () => true, send: async () => ({ results: [] }) })
  await storage.put(attempt(2)); partial.start()
  await assert.rejects(partial.flush(), /Missing attempt acknowledgement/)
  assert.deepEqual(await storage.list(), [attempt(2)])
})

test('storage failure rejects seam; no false saved acknowledgement', async () => {
  const queue = createAttemptQueue({ ...store(), put: async () => { throw new Error('quota') } }, { online: () => false })
  queue.start(); await queue.flush()
  await assert.rejects(queue.enqueue(attempt(1)), /quota/)
  assert.equal(queue.getSnapshot().pending, 0)
})

test('two queue instances sharing cross-tab lock cannot read/send same stored UUID twice', async () => {
  const storage = store(); await storage.put(attempt(1))
  let tail = Promise.resolve(); const batches = []
  const lock = work => { const result = tail.then(work); tail = result.catch(() => undefined); return result }
  const options = { online: () => true, lock, send: async batch => { batches.push(batch); await tick(); return applied(batch) } }
  const tab1 = createAttemptQueue(storage, options); const tab2 = createAttemptQueue(storage, options)
  tab1.start(); tab2.start()
  await Promise.all([tab1.flush(), tab2.flush()])
  assert.deepEqual(batches, [[attempt(1)]])
})

test('answers added during slow flush are durably drained by same trigger', async () => {
  const storage = store(); const sent = []; let release
  const gate = new Promise(resolve => { release = resolve })
  const queue = createAttemptQueue(storage, { online: () => true, send: async batch => {
    sent.push(batch); if (sent.length === 1) await gate; return applied(batch)
  } })
  queue.start(); await queue.flush()
  const first = queue.enqueue(attempt(1)); const end = queue.flush()
  await tick()
  const next = queue.enqueue(attempt(2)); release()
  await Promise.all([first, next, end])
  assert.equal(sent.flat().length, 2)
  assert.equal(new Set(sent.flat().map(a => a.clientAttemptId)).size, 2)
})
