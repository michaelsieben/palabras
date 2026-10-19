import test from 'node:test'
import assert from 'node:assert/strict'
import { ApiError } from '../src/api/client.ts'
import { getReviewQueue, getApprovedWords, parseReviewQueue, postReviewActions, isApprovedWord } from '../src/api/review.ts'
import { reviewWord, approvedWord, reviewAction } from './reviewFixtures.mjs'

test('review fetchers preserve camelCase, current approved version, paths, cookie/header and abort boundary', async t => {
  const calls = []; const controller = new AbortController()
  t.mock.method(globalThis, 'fetch', async (path, init) => {
    calls.push({ path, init })
    return Response.json(path.includes('/approved') ? { items: Array.from({ length: 30 }, (_, i) => approvedWord(i + 1)) } : path.includes('/queue') ? { items: [reviewWord()], pendingTotal: 1, topics: [{ topic: 'Casa', pending: 1 }] } : { results: [{ clientReviewId: reviewAction().clientReviewId, result: 'stale_version' }] })
  })
  assert.equal((await getReviewQueue(controller.signal)).items[0].version, 3)
   assert.equal(calls[0].path, '/api/review/queue?limit=30&status=pending'); assert.equal(calls[0].init.signal, controller.signal)
  const approved = await getApprovedWords('café y té', '17', controller.signal)
  assert.equal(approved.items[0].version, 7); assert.equal(approved.nextCursor, '30')
   assert.equal(calls[1].path, '/api/review/approved?q=caf%C3%A9+y+t%C3%A9&limit=30&status=approved&after=17')
  assert.equal((await postReviewActions([reviewAction()])).results[0].result, 'stale_version')
  assert.equal(calls[2].init.headers.get('X-Palabras'), '1'); assert.equal(calls[2].init.credentials, 'include')
  assert.deepEqual(JSON.parse(calls[2].init.body), { actions: [reviewAction()] })
})
test('review guards reject malformed nested content and omitted/nonpositive approved versions', () => {
  const valid = { items: [reviewWord()], pendingTotal: 1, topics: [] }
  assert.deepEqual(parseReviewQueue(valid), valid)
  for (const override of [{ version: 0 }, { senses: [null] }, { senses: [] }, { alternatives: [{ id: 1, field: 'variant', text: '', reason: '' }] }, { agreement: 1.1 }, { band: 'baja' }, { lintWarnings: [1] }, { lastFlag: { action: 'reject', at: 'bad' } }]) assert.throws(() => parseReviewQueue({ ...valid, items: [reviewWord(1, override)] }), ApiError)
  for (const version of [undefined, null, 0, 1.2, '3']) assert.equal(isApprovedWord(approvedWord(1, { version })), false)
  assert.equal(parseReviewQueue({ ...valid, items: [reviewWord(1, { alternatives: [{ id: 1, field: 'gloss', text: 'home', reason: 'council', support: '3/4' }] })] }).items[0].alternatives[0].support, '3/4')
})
test('review results retain all distinct per-item failures inside HTTP200; malformed ack rejects', async t => {
  for (const result of ['applied', 'duplicate', 'invalid', 'invalid_transition', 'stale_version']) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ results: [{ clientReviewId: 'id', result }] }))
    assert.equal((await postReviewActions([])).results[0].result, result)
  }
  for (const result of [{ clientReviewId: 'id', result: 'unknown' }, { clientReviewId: 'id', result: 'applied', word: { status: 'approved', version: 0 } }]) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ results: [result] }))
    await assert.rejects(postReviewActions([]), ApiError)
  }
})
test('approved final page exposes no next cursor; stale cursor 400 stays identifiable', async t => {
  t.mock.method(globalThis, 'fetch', async () => Response.json({ items: [approvedWord()] }))
  assert.equal((await getApprovedWords('')).nextCursor, undefined)
  t.mock.method(globalThis, 'fetch', async () => Response.json({ error: 'bad_request' }, { status: 400 }))
  await assert.rejects(getApprovedWords('', '3'), e => e instanceof ApiError && e.status === 400)
})
test('queue scopes explicitly serialize status and rejected cursor; retain authoritative status/version/counts', async t => {
  const calls = []
  const body = { items: [reviewWord(9, { status: 'rejected', version: 12 })], pendingTotal: 0, topics: [], rejectedTotal: 31, approvedTotal: 0, nextAfter: 9 }
  t.mock.method(globalThis, 'fetch', async path => {
    calls.push(path)
    const status = new URL(path, 'http://localhost').searchParams.get('status')
    return Response.json({ ...body, items: [reviewWord(9, { status: status === 'pending' ? 'pending_review' : status === 'all' ? 'rejected' : status, version: 12 })] })
  })
  for (const scope of ['pending', 'approved', 'rejected', 'all']) {
    const result = await getReviewQueue(undefined, scope, '10')
    assert.equal(result.items[0].status, scope === 'pending' ? 'pending_review' : scope === 'all' ? 'rejected' : scope); assert.equal(result.items[0].version, 12)
    assert.equal(result.rejectedTotal, 31); assert.equal(result.nextAfter, 9)
    assert.equal(calls.at(-1), `/api/review/queue?limit=30&status=${scope}&after=10`)
  }
})
test('decided queue scopes reject omitted or mismatched status rather than inventing an approved state', async t => {
  for (const status of [undefined, 'approved']) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ items: [reviewWord(1, { status })], pendingTotal: 0, topics: [] }))
    await assert.rejects(getReviewQueue(undefined, 'rejected'), ApiError)
  }
})
test('queue guards reject invalid statuses/counts/cursors but accept old cached pending payloads', () => {
  const body = { items: [reviewWord()], pendingTotal: 1, topics: [] }
  assert.deepEqual(parseReviewQueue(body), body)
  for (const override of [{ nextAfter: 0 }, { nextAfter: '2' }, { rejectedTotal: -1 }, { approvedTotal: '4' }, { items: [reviewWord(1, { status: 'draft' })] }]) assert.throws(() => parseReviewQueue({ ...body, ...override }), ApiError)
})
