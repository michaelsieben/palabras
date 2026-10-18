import test from 'node:test'
import assert from 'node:assert/strict'
import { createMemoryEnqueue } from '../src/learn/memoryAttempts.ts'

test('enqueue seam serializes real attempts and coalesces duplicate submissions', async t => {
  const ids = []
  t.mock.method(globalThis, 'fetch', async (_, init) => {
    const id = JSON.parse(init.body).attempts[0].clientAttemptId
    ids.push(id)
    return Response.json({ results: [{ clientAttemptId: id, status: 'applied' }] })
  })
  const enqueue = createMemoryEnqueue()
  const one = { clientAttemptId: 'one' }
  const first = enqueue(one)
  assert.equal(enqueue(one), first)
  const second = enqueue({ clientAttemptId: 'two' })
  await Promise.all([first, second])
  assert.deepEqual(ids, ['one', 'two'])
  await enqueue(one)
  assert.deepEqual(ids, ['one', 'two'])
})

test('network/missing acknowledgements preserve attempt identity for explicit retry', async t => {
  const attempts = []
  let calls = 0
  t.mock.method(globalThis, 'fetch', async (_, init) => {
    attempts.push(JSON.parse(init.body).attempts[0])
    if (++calls === 1) throw new TypeError('offline')
    if (calls === 2) return Response.json({ results: [] })
    return Response.json({ results: [{ clientAttemptId: 'same-id', status: 'duplicate' }] })
  })
  const enqueue = createMemoryEnqueue()
  const answer = { clientAttemptId: 'same-id', answerText: 'café', rating: 3 }
  await assert.rejects(enqueue(answer))
  await assert.rejects(enqueue(answer), /Missing attempt acknowledgement/)
  assert.equal((await enqueue(answer)).status, 'duplicate')
  assert.deepEqual(attempts, [answer, answer, answer])
})
