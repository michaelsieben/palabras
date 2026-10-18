import test from 'node:test'
import assert from 'node:assert/strict'
import { getSessionPlan, postAttempts, getLearnerWords } from '../src/api/learn.ts'
import { ApiError } from '../src/api/client.ts'

test('learn client uses frozen paths, camelCase shapes, abort and mutation boundary', async t => {
  const requests = []
  const plan = { sessionId: 'session', generatedAtUtc: '2026-10-18T12:00:00Z', localDate: '2026-10-18', items: [], counts: { due: 0, newOffered: 0, backlog: 0 }, warnings: [] }
  t.mock.method(globalThis, 'fetch', async (path, init) => {
    requests.push({ path, init })
    return Response.json(path.includes('/plan') ? plan : path.includes('/words') ? { items: [] } : { results: [{ clientAttemptId: 'id', status: 'duplicate' }] })
  })
  const signal = new AbortController().signal
  assert.deepEqual(await getSessionPlan({ maxNew: 12, budgetS: 600 }, signal), plan)
  assert.equal(requests[0].path, '/api/learn/session/plan?maxNew=12&budgetS=600')
  assert.equal(requests[0].init.signal, signal)
  assert.deepEqual(await postAttempts([{ clientAttemptId: 'id', rating: 3 }]), { results: [{ clientAttemptId: 'id', status: 'duplicate' }] })
  assert.equal(requests[1].path, '/api/learn/attempts')
  assert.equal(requests[1].init.headers.get('X-Palabras'), '1')
  assert.deepEqual(JSON.parse(requests[1].init.body), { attempts: [{ clientAttemptId: 'id', rating: 3 }] })
  assert.deepEqual(await getLearnerWords('café', 'food'), [])
  assert.equal(requests[2].path, '/api/learn/words?q=caf%C3%A9&topic=food')
})

test('malformed successful learn responses reject instead of crashing UI', async t => {
  for (const request of [() => getSessionPlan(), () => postAttempts([]), () => getLearnerWords('', '')]) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ items: [null], results: [{ status: 'unknown' }] }))
    await assert.rejects(request(), error => error instanceof ApiError && error.kind === 'invalid-response')
  }
})
