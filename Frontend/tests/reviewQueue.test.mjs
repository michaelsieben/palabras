import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'
import { ApiError } from '../src/api/client.ts'
import { createReviewQueue } from '../src/review/reviewQueue.ts'
import { createReviewStorage, reviewDatabaseName } from '../src/review/storage.ts'
import { reviewWord, reviewAction, memoryReviewStore, appliedReviews } from './reviewFixtures.mjs'
const tick = () => new Promise(resolve => setImmediate(resolve))
const row = n => ({ action: reviewAction(n), word: reviewWord(n), state: 'pending' })
test('refresh cleanup preserves durable pending rows even when another tab has not published its retry', async () => {
  const store = memoryReviewStore(); const queue = createReviewQueue(store, { online: () => false })
  const failed = { ...row(7), state: 'failed', result: 'invalid' }
  await store.put(failed); await queue.flush()
  await store.put({ ...failed, state: 'pending', result: undefined })
  assert.equal(queue.getSnapshot().rows[0].state, 'failed', 'simulate stale tab snapshot')
  await queue.discard(failed.action.clientReviewId, true)
  assert.equal((await store.list())[0].state, 'pending')
  await queue.discard(failed.action.clientReviewId)
  assert.deepEqual(await store.list(), [], 'explicit discard remains deliberate')
})
test('invalid_transition stays durably failed across flush, explicit retry and recreation until refreshed or discarded', async () => {
  const store = memoryReviewStore(); const action = reviewAction(7, { action: 'reject' }); let calls = 0
  await store.put({ action, word: reviewWord(7, { status: 'approved' }), state: 'pending' })
  const options = { online: () => true, send: async () => { calls++; return { results: [{ clientReviewId: action.clientReviewId, result: 'invalid_transition' }] } } }
  const queue = createReviewQueue(store, options); queue.start(); await queue.flush()
  await queue.retry(action.clientReviewId); await queue.flush(); queue.stop()
  const reloaded = createReviewQueue(store, options); reloaded.start(); await reloaded.flush(); await reloaded.retry(action.clientReviewId); await reloaded.flush(); reloaded.stop()
  assert.equal(calls, 1)
  assert.equal(reloaded.getSnapshot().rows[0].state, 'failed')
  assert.equal(reloaded.getSnapshot().rows[0].result, 'invalid_transition')
  assert.deepEqual(reloaded.getSnapshot().rows[0].action, action)
})
for (const action of ['reject', 'approve']) test(`objection ${action} survives offline recreation and retry with original UUID/version`, async () => {
  const store = memoryReviewStore(); const sent = []; const original = reviewWord(9, { status: action === 'reject' ? 'approved' : 'rejected', version: 14 })
  const verdict = reviewAction(9, { action, wordVersion: original.version })
  const offline = createReviewQueue(store, { online: () => false }); offline.start()
  await offline.enqueue(verdict, original); await offline.flush(); offline.stop()
  const reloaded = createReviewQueue(store, { online: () => true, send: async batch => { sent.push(batch); if (sent.length === 1) throw new ApiError('http', 503); return { results: batch.map(a => ({ clientReviewId: a.clientReviewId, result: 'duplicate' })) } } })
  reloaded.start(); await reloaded.flush()
  assert.equal(reloaded.getSnapshot().rows[0].state, 'pending'); assert.deepEqual(reloaded.getSnapshot().rows[0].word, original)
  await reloaded.flush(); assert.deepEqual(sent, [[verdict], [verdict]])
  assert.equal(reloaded.getSnapshot().rows[0].result, 'duplicate'); reloaded.stop()
})
test('offline chip verdict persists full original/version/alternative, survives recreation, same UUID replays once', async () => {
  const store = memoryReviewStore(); const sent = []
  const action = reviewAction(1, { action: 'approve_alt', field: 'lemma', alternativeId: 10 })
  const offline = createReviewQueue(store, { online: () => false }); offline.start()
  await offline.enqueue(action, reviewWord()); await offline.flush(); offline.stop()
  assert.deepEqual((await store.list())[0], { action, word: reviewWord(), state: 'pending' })
  const reconnect = createReviewQueue(store, { online: () => true, send: async b => { sent.push(b); return appliedReviews(b) } })
  reconnect.start(); await Promise.all([reconnect.flush(), reconnect.flush()]); await reconnect.flush()
  assert.deepEqual(sent, [[action]]); assert.equal(reconnect.getSnapshot().rows[0].state, 'applied')
})
test('batch-approve-10 fixture proof: strict alta, UUID per word, viaBatch, one request, durable atomic batch', async () => {
  const store = memoryReviewStore(); const batches = []; let online = false
  const queue = createReviewQueue(store, { online: () => online, send: async b => { batches.push(b); return appliedReviews(b) } }); queue.start()
  const entries = Array.from({ length: 10 }, (_, i) => ({ action: reviewAction(i + 1, { viaBatch: true }), word: reviewWord(i + 1, { band: 'alta' }) }))
  await queue.enqueueMany(entries); await queue.flush()
  assert.equal((await store.list()).length, 10); online = true; await queue.flush()
  assert.equal(batches.length, 1); assert.equal(batches[0].length, 10); assert.equal(new Set(batches[0].map(a => a.clientReviewId)).size, 10)
  assert.ok(batches[0].every(a => a.viaBatch && a.wordVersion === 3)); assert.ok(queue.getSnapshot().rows.every(r => r.state === 'applied'))
})
test('all failed per-item outcomes persist per row; explicit retry reuses UUID, never loops', async () => {
  const store = memoryReviewStore(); const outcomes = ['applied', 'duplicate', 'invalid', 'invalid_transition', 'stale_version']; let calls = 0
  for (let n = 1; n <= 5; n++) await store.put(row(n))
  const queue = createReviewQueue(store, { online: () => true, send: async b => { calls++; return { results: b.map((a, i) => ({ clientReviewId: a.clientReviewId, result: calls === 1 ? outcomes[i] : 'applied' })) } } }); queue.start(); await queue.flush()
  assert.deepEqual(queue.getSnapshot().rows.map(r => r.state), ['applied', 'applied', 'failed', 'failed', 'failed'])
  await queue.flush(); assert.equal(calls, 1)
  await queue.retry(reviewAction(3).clientReviewId); await queue.flush(); assert.equal(calls, 2)
  assert.equal(queue.getSnapshot().rows[2].action.clientReviewId, reviewAction(3).clientReviewId)
})
test('corrupt HTTP500 batch isolated into singles; pending row replays with same UUID after recovery', async () => {
  const store = memoryReviewStore(); for (let n = 1; n <= 3; n++) await store.put(row(n))
  const sent = []; let corrupt = true
  const queue = createReviewQueue(store, { online: () => true, send: async b => { sent.push(b); if (corrupt && (b.length > 1 || b[0].wordId === 2)) throw new ApiError('http', 500); return appliedReviews(b) } }); queue.start(); await queue.flush()
  assert.deepEqual(sent.map(b => b.map(a => a.wordId)), [[1, 2, 3], [1], [2], [3]])
  assert.deepEqual(queue.getSnapshot().rows.map(r => r.state), ['applied', 'pending', 'applied'])
  assert.deepEqual(sent[0].map(a => a.clientReviewId), sent.slice(1).flat().map(a => a.clientReviewId))
  corrupt = false; await queue.flush()
  assert.deepEqual(sent[4], [reviewAction(2)])
  assert.ok(queue.getSnapshot().rows.every(r => r.state === 'applied'))
})
test('missing/duplicate acknowledgements fail actionable row without treating it as success', async () => {
  for (const duplicate of [false, true]) {
    const store = memoryReviewStore(); await store.put(row(1))
    const queue = createReviewQueue(store, { online: () => true, send: async b => ({ results: duplicate ? [...appliedReviews(b).results, ...appliedReviews(b).results] : [] }) }); queue.start(); await queue.flush(); await queue.flush()
    assert.equal(queue.getSnapshot().rows[0].state, 'failed'); assert.equal(queue.getSnapshot().rows[0].error, 'missing-acknowledgement')
  }
})
test('network and HTTP502/503 stay pending for replay; HTTP400/403 stay actionable failures', async () => {
  for (const [error, state] of [[new ApiError('network'), 'pending'], [new ApiError('http', 502), 'pending'], [new ApiError('http', 503), 'pending'], [new ApiError('http', 400), 'failed'], [new ApiError('forbidden', 403), 'failed']]) {
    const store = memoryReviewStore(); await store.put(row(1)); let calls = 0
    const queue = createReviewQueue(store, { online: () => true, send: async () => { calls++; throw error } }); queue.start(); await queue.flush()
    assert.equal(calls, 1); assert.equal(queue.getSnapshot().rows[0].state, state)
  }
})
test('storage failure rejects enqueue with no false saved verdict; duplicate word blocked', async () => {
  const store = memoryReviewStore(); const queue = createReviewQueue({ ...store, put: async () => { throw new Error('quota') } }, { online: () => false }); queue.start(); await queue.flush()
  await assert.rejects(queue.enqueue(reviewAction(), reviewWord()), /quota/); assert.equal(queue.getSnapshot().rows.length, 0); assert.equal(queue.getSnapshot().storageError, true)
  const good = createReviewQueue(store, { online: () => false }); await good.enqueue(reviewAction(), reviewWord())
  await assert.rejects(good.enqueue(reviewAction(2, { wordId: 1 }), reviewWord()), /already queued/)
})
test('cross-tab lock, overlapping writes and stop cannot deadlock or duplicate submission', async () => {
  const store = memoryReviewStore(); let tail = Promise.resolve(); const sent = []
  const lock = work => { const p = tail.then(work); tail = p.catch(() => undefined); return p }
  let release; const gate = new Promise(r => { release = r })
  const options = { lock, online: () => true, send: async b => { sent.push(b); if (sent.length === 1) await gate; return appliedReviews(b) } }
  const one = createReviewQueue(store, options); const two = createReviewQueue(store, options)
  await store.put(row(1)); one.start(); two.start(); await tick()
  const write = one.enqueue(reviewAction(2), reviewWord(2)); one.stop(); release(); await write
  await Promise.all([one.flush(), two.flush()]); await tick()
  assert.equal(sent.flat().filter(a => a.wordId === 1).length, 1)
})
test('production account switch hides and never replays account A actions from shared IndexedDB factory', async () => {
  const databases = new Map(); const opened = []; const sent = []; let online = false
  // One shared factory: production DB name and queue cache select the account, not separate test stores.
  const factory = { open(name) {
    opened.push(name)
    const request = {}
    queueMicrotask(() => {
      const fresh = !databases.has(name)
      if (fresh) databases.set(name, new Map())
      const tables = databases.get(name)
      request.result = {
        createObjectStore(table) { tables.set(table, new Map()) },
        transaction(table) {
          const rows = tables.get(table)
          const tx = { objectStore: () => ({
            put(value) { rows.set(value.action.clientReviewId, structuredClone(value)) },
            getAll() { return { result: structuredClone([...rows.values()]) } },
            delete(id) { rows.delete(id) },
          }) }
          queueMicrotask(() => tx.oncomplete())
          return tx
        },
        close() {},
      }
      if (fresh) request.onupgradeneeded()
      request.onsuccess()
    })
    return request
  } }
  const exports = {}
  const source = ts.transpileModule(readFileSync(new URL('../src/review/reviewQueue.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText
  runInNewContext(source, { exports, navigator: { get onLine() { return online }, locks: { request: async (_name, work) => work() } }, require: name => ({
    '../api/client.ts': { ApiError }, '../api/review.ts': { postReviewActions: async actions => { sent.push(actions); return appliedReviews(actions) } },
    './storage.ts': { reviewDatabaseName, createReviewStorage: name => createReviewStorage(name, factory) },
  })[name] })
  const accountA = exports.getReviewQueueStore('a:b'); accountA.start()
  await accountA.enqueue(reviewAction(1), reviewWord(1)); await accountA.flush(); accountA.stop()
  online = true
  const accountB = exports.getReviewQueueStore('a%3Ab'); accountB.start(); await accountB.flush()
  assert.deepEqual([...accountB.getSnapshot().rows], [])
  assert.deepEqual(sent, [])
  await accountB.enqueue(reviewAction(2), reviewWord(2)); await accountB.flush(); accountB.stop()
  assert.deepEqual(sent.flat().map(a => a.clientReviewId), [reviewAction(2).clientReviewId])
  assert.deepEqual(accountB.getSnapshot().rows.map(r => r.word.wordId), [2])
  assert.deepEqual(opened, [reviewDatabaseName('a:b'), reviewDatabaseName('a%3Ab')])
  assert.equal(accountA.getSnapshot().rows[0].state, 'pending')
  assert.equal(exports.getReviewQueueStore('a:b'), accountA)
  accountA.start(); await accountA.flush(); accountA.stop()
  assert.deepEqual(sent.flat().map(a => a.clientReviewId), [reviewAction(2).clientReviewId, reviewAction(1).clientReviewId])
})
test('viaBatch request cap25 and overall cap50; 26-entry user batch rejected', async () => {
  const store = memoryReviewStore(); for (let n = 1; n <= 61; n++) await store.put({ ...row(n), action: reviewAction(n, { viaBatch: true }) })
  const sent = []; const queue = createReviewQueue(store, { online: () => true, send: async b => { sent.push(b); return appliedReviews(b) } }); queue.start(); await queue.flush()
  assert.deepEqual(sent.map(b => b.length), [25, 25, 11])
  await assert.rejects(queue.enqueueMany(Array.from({ length: 26 }, (_, i) => ({ action: reviewAction(i + 100), word: reviewWord(i + 100) }))), /Invalid queued batch/)
})
