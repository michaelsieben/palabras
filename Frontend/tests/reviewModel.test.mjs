import test from 'node:test'
import assert from 'node:assert/strict'
import { ApiError } from '../src/api/client.ts'
import { createCachedQueue } from '../src/review/cachedQueue.ts'
import { batchEligible, toggleBatch, parseSittingSize, sittingKey, editValues, changedEdits } from '../src/review/reviewModel.ts'
import { reviewWord } from './reviewFixtures.mjs'
test('sitting default15, bounds5–30, account scope and batch strict eligibility/cap25', () => {
  for (const invalid of [null, '', '4', '31', '5.5', 'bad']) assert.equal(parseSittingSize(invalid), 15)
  for (const n of [5, 15, 30]) assert.equal(parseSittingSize(String(n)), n)
  assert.notEqual(sittingKey('alice'), sittingKey('bob'))
  assert.equal(batchEligible(reviewWord()), false); assert.equal(batchEligible(reviewWord(1, { band: 'alta' })), true)
  assert.equal(batchEligible(reviewWord(1, { band: 'alta', lintWarnings: ['warning'] })), false)
  let selected = []; for (let n = 1; n <= 26; n++) selected = toggleBatch(selected, n)
  assert.equal(selected.length, 25); assert.equal(toggleBatch(selected, 1).length, 24)
})
test('edit payload only changed supported columns, preserves sense order; no no-op/EN fabricated edits', () => {
  const original = editValues(reviewWord())
  assert.equal(changedEdits(original, original).length, 0)
  const draft = original.map(e => e.field === 'gloss' ? { ...e, after: 'changed' } : e)
  assert.deepEqual(changedEdits(original, draft), [{ field: 'gloss', senseOrder: 0, after: 'changed' }])
  assert.ok(!original.some(e => e.field === 'exampleEn'))
})
test('cache full alternatives guard, offline load; auth/HTTP errors never bypass via cache', async () => {
  const data = { items: [reviewWord()], pendingTotal: 1, topics: [] }; let stored
  const storage = { read: async () => stored, write: async d => { stored = structuredClone(d) } }
  await createCachedQueue(storage, async () => data).load()
  const offline = await createCachedQueue(storage, async () => { throw new ApiError('network') }).load()
  assert.deepEqual(offline.data, data); assert.equal(offline.offline, true)
  for (const error of [new ApiError('unauthorized', 401), new ApiError('forbidden', 403), new ApiError('http', 500), new ApiError('invalid-response')]) await assert.rejects(createCachedQueue(storage, async () => { throw error }).load(), e => e === error)
  stored = { ...data, items: [null] }; await assert.rejects(createCachedQueue(storage, async () => { throw new ApiError('network') }).load(), ApiError)
})
test('cache quota does not prevent online work; aborted fetch never reads cache', async () => {
  const data = { items: [reviewWord()], pendingTotal: 1, topics: [] }
  const cache = createCachedQueue({ read: async () => { throw new Error('must not read') }, write: async () => { throw new Error('quota') } }, async () => data)
  assert.equal((await cache.load()).cacheFailed, true)
  const c = new AbortController(); c.abort()
  await assert.rejects(createCachedQueue({ read: async () => data, write: async () => {} }, async () => { throw new ApiError('network') }).load(c.signal), ApiError)
})
