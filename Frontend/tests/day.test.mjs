import test from 'node:test'
import assert from 'node:assert/strict'
import { ApiError } from '../src/api/client.ts'
import { completeDay, getDayStatus, parseDayStatus, parseSessionPlan, skipDay } from '../src/api/learn.ts'
import { sessionDayCompletion, countsAsCompletedDay, createCompletionQueue } from '../src/learn/completionQueue.ts'
import { createAttemptQueue } from '../src/learn/attemptQueue.ts'

const day = { localDate: '2026-10-18', status: 'done', streak: 4, skipAvailable: true, skipNextAvailable: null }
const body = { localDate: day.localDate, durationS: 600, newCount: 1, reviewCount: 2 }
const tick = () => new Promise(resolve => setImmediate(resolve))
function store() {
  const rows = new Map()
  return { list: async () => structuredClone([...rows.values()]), put: async row => { rows.set(row.localDate, structuredClone(row)) } }
}
const answers = { flush: async () => {}, getSnapshot: () => ({ pending: 0, error: false }) }

test('completion echoes server session day across midnight, DST and arbitrary learner zones', () => {
  for (const localDate of ['2026-10-18', '2026-10-25', '2026-03-29', '2026-10-17']) {
    const plan = { localDate, items: [{ cardId: 1, isNew: true }, { cardId: 2, isNew: false }, { cardId: 3, isNew: false }] }
    const attempts = [{ cardId: 1, durationMs: 1200 }, { cardId: 1, durationMs: 800 }, { cardId: 2, durationMs: 3000 }]
    assert.deepEqual(sessionDayCompletion(plan, attempts), { localDate, durationS: 5, newCount: 1, reviewCount: 1 })
  }
})
test('invalid server plan date cannot be cached or sent as a completion date', () => {
  const plan = { sessionId: 'session', generatedAtUtc: '2026-10-18T12:00:00Z', localDate: '2026-10-18', items: [], counts: { due: 0, newOffered: 0, backlog: 0 }, warnings: [] }
  assert.deepEqual(parseSessionPlan(plan), plan)
  for (const localDate of ['2026-02-30', '2026-10-18T12:00:00Z', '', null]) assert.throws(() => parseSessionPlan({ ...plan, localDate }), /invalid-response/)
})
test('only full sessions or an early stop with at least 300s measured active time count', () => {
  assert.equal(countsAsCompletedDay(false, [{ durationMs: 1 }]), true)
  assert.equal(countsAsCompletedDay(true, [{ durationMs: 299999 }]), false)
  assert.equal(countsAsCompletedDay(true, [{ durationMs: 150000 }, { durationMs: 150000 }]), true)
})
test('day status validates nulls, enums, nonnegative streak and exact calendar dates', () => {
  assert.deepEqual(parseDayStatus(day), day)
  assert.deepEqual(parseDayStatus({ ...day, status: null }), { ...day, status: null })
  for (const invalid of [{ status: 'frozen' }, { streak: -1 }, { localDate: '2026-02-30' }, { skipNextAvailable: undefined }, { skipAvailable: 1 }]) {
    assert.throws(() => parseDayStatus({ ...day, ...invalid }), /invalid-response/)
  }
})
test('API day routes preserve camelCase, localDate and mutation header; 409 is a typed outcome', async () => {
  const old = globalThis.fetch; const calls = []
  try {
    globalThis.fetch = async (path, init) => {
      calls.push({ path, init })
      if (path.endsWith('/skip')) return Response.json({ reason: 'skip_unavailable', nextAvailable: '2026-10-25' }, { status: 409 })
      return Response.json(day)
    }
    assert.deepEqual(await getDayStatus(), day)
    assert.deepEqual(await completeDay(body), day)
    assert.deepEqual(await skipDay(day.localDate), { kind: 'skip_unavailable', nextAvailable: '2026-10-25' })
    assert.deepEqual(JSON.parse(calls[1].init.body), body)
    assert.deepEqual(JSON.parse(calls[2].init.body), { localDate: day.localDate, source: 'app' })
    assert.equal(calls[1].init.headers.get('X-Palabras'), '1')
    assert.equal(calls[1].init.credentials, 'include')
    globalThis.fetch = async () => Response.json({ reason: 'already_done' }, { status: 409 })
    assert.deepEqual(await skipDay(day.localDate), { kind: 'already_done' })
    globalThis.fetch = async () => Response.json({ reason: 'skip_unavailable', next_available: '2026-10-25' }, { status: 409 })
    await assert.rejects(skipDay(day.localDate), /API: http/)
  } finally { globalThis.fetch = old }
})
test('offline completion survives recreation, original date retained, tombstone dedupes reload and rounds', async () => {
  const storage = store(); const sent = []
  const offline = createCompletionQueue(storage, answers, { online: () => false })
  offline.start(); await offline.enqueue(body); await offline.flush()
  assert.equal(offline.getSnapshot().pending, 1); offline.stop()
  const options = { online: () => true, send: async value => { sent.push(value); return day } }
  const reload = createCompletionQueue(storage, answers, options)
  reload.start(); await reload.flush()
  assert.deepEqual(sent, [body]); assert.equal(reload.getSnapshot().pending, 0)
  reload.stop()
  const again = createCompletionQueue(storage, answers, options)
  again.start(); await again.enqueue({ ...body, durationS: 10 }); await again.flush()
  assert.deepEqual(sent, [body]); assert.equal((await storage.list())[0].state, 'sent')
})
test('completion flush waits for answer acknowledgements, failure retains durable completion', async () => {
  const storage = store(); await storage.put({ localDate: body.localDate, body, state: 'pending' })
  const events = []; let release
  const gate = new Promise(resolve => { release = resolve })
  const queue = createCompletionQueue(storage, { ...answers, flush: async () => { events.push('answers'); await gate } }, {
    online: () => true, send: async () => { events.push('done'); return day },
  })
  queue.start(); const flush = queue.flush(); await tick()
  assert.deepEqual(events, ['answers']); release(); await flush
  assert.deepEqual(events, ['answers', 'done'])
  const failing = createCompletionQueue(store(), { ...answers, flush: async () => { throw new Error('network') } }, { online: () => true })
  await failing.enqueue(body); failing.start(); await assert.rejects(failing.flush(), /network/)
  assert.equal(failing.getSnapshot().pending, 1)
})
test('lost completion response retries original day; racing flushes coalesce', async () => {
  const storage = store(); let fail = true; const sent = []
  const queue = createCompletionQueue(storage, answers, { online: () => true, send: async value => {
    sent.push(value); if (fail) throw new ApiError('network'); return day
  } })
  await queue.enqueue(body); queue.start(); await assert.rejects(queue.flush(), /network/)
  assert.equal(queue.getSnapshot().pending, 1); fail = false
  const one = queue.flush(); const two = queue.flush(); assert.equal(one, two); await one
  assert.deepEqual(sent, [body, body]); assert.equal((await storage.list())[0].state, 'sent')
})
test('400 rejection stays visible across reload, never reattributed to today; later day drains', async () => {
  const storage = store(); await storage.put({ localDate: '2026-10-15', body: { localDate: '2026-10-15' }, state: 'pending' })
  const sent = []
  const options = { online: () => true, send: async value => {
    sent.push(value.localDate); if (value.localDate === '2026-10-15') throw new ApiError('http', 400, { error: 'bad_request' }); return day
  } }
  const queue = createCompletionQueue(storage, answers, options)
  await queue.enqueue(body); queue.start(); await queue.flush()
  assert.deepEqual(sent, ['2026-10-15', body.localDate]); assert.equal(queue.getSnapshot().rejected, 1)
  queue.stop(); const reload = createCompletionQueue(storage, answers, options); reload.start(); await reload.flush()
  assert.equal(reload.getSnapshot().rejected, 1); assert.equal(sent.length, 2)
})
test('two instances use shared lock for enqueue and send, one wire completion', async () => {
  const storage = store(); const sent = []; let tail = Promise.resolve()
  const lock = work => { const result = tail.then(work); tail = result.catch(() => {}); return result }
  const options = { online: () => true, lock, send: async value => { sent.push(value); await tick(); return day } }
  const first = createCompletionQueue(storage, answers, options); const second = createCompletionQueue(storage, answers, options)
  await Promise.all([first.enqueue(body), second.enqueue(body)])
  first.start(); second.start(); await Promise.all([first.flush(), second.flush()])
  assert.deepEqual(sent, [body])
})
test('completion cannot claim saved when storage write fails; attempts expose durable failure', async () => {
  const queue = createCompletionQueue({ ...store(), put: async () => { throw new Error('quota') } }, answers, { online: () => false })
  await assert.rejects(queue.enqueue(body), /quota/)
  assert.equal(queue.getSnapshot().pending, 0)
  const attempts = createAttemptQueue({ put: async () => { throw new Error('quota') }, list: async () => [], remove: async () => {} }, { online: () => false })
  void attempts.enqueue({ clientAttemptId: 'one' }).catch(() => {})
  await assert.rejects(attempts.persisted(), /Answers not stored/)
})
test('pending answer acknowledgements and auth failures never discard completion', async () => {
  for (const option of [
    { attempts: { ...answers, getSnapshot: () => ({ pending: 1, error: false }) }, send: async () => { assert.fail('must not send') } },
    { attempts: answers, send: async () => { throw new ApiError('unauthorized', 401) } },
  ]) {
    const storage = store()
    const queue = createCompletionQueue(storage, option.attempts, { online: () => true, send: option.send })
    await queue.enqueue(body); queue.start(); await assert.rejects(queue.flush())
    assert.equal(queue.getSnapshot().pending, 1); assert.equal((await storage.list())[0].state, 'pending')
    queue.stop()
  }
})
test('completion enqueued during a slow send is drained without another online event', async () => {
  const storage = store(); const sent = []; let release
  const gate = new Promise(resolve => { release = resolve })
  const queue = createCompletionQueue(storage, answers, { online: () => true, send: async value => {
    sent.push(value.localDate); if (sent.length === 1) await gate; return { ...day, localDate: value.localDate }
  } })
  await queue.enqueue(body); queue.start(); const first = queue.flush(); await tick()
  const second = queue.enqueue({ ...body, localDate: '2026-10-19' }); release()
  await Promise.all([first, second]); await tick(); await queue.flush()
  assert.deepEqual(sent, ['2026-10-18', '2026-10-19'])
  assert.equal(queue.getSnapshot().pending, 0)
})
