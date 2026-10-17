import test from 'node:test'
import assert from 'node:assert/strict'
import { ApiError, apiRequest, login, logout, me, onUnauthorized } from '../src/api/client.ts'
import { loginDestination, routeAccess } from '../src/routeAccess.ts'

test('API calls include cookies, mutation header and documented JSON bodies', async t => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (path, init) => {
    requests.push({ path, init })
    return path.endsWith('/logout') ? new Response(null, { status: 204 }) : Response.json({ username: 'ana', roles: ['reviewer'] })
  })
  assert.deepEqual(await me(), { username: 'ana', roles: ['reviewer'] })
  assert.deepEqual(await login({ username: 'ana', password: 'secret' }), { username: 'ana', roles: ['reviewer'] })
  assert.equal(await logout(), undefined)
  assert.deepEqual(requests.map(({ path }) => path), ['/api/auth/me', '/api/auth/login', '/api/auth/logout'])
  for (const { init } of requests) {
    assert.equal(init.credentials, 'include')
    assert.equal(init.redirect, 'error')
    assert.equal(init.headers.get('Accept'), 'application/json')
    assert.equal(init.headers.get('X-Palabras'), init.method === 'GET' ? null : '1')
  }
  assert.equal(requests[1].init.headers.get('Content-Type'), 'application/json')
  assert.deepEqual(JSON.parse(requests[1].init.body), { username: 'ana', password: 'secret' })
  await apiRequest('/api/test', { method: 'patch', credentials: 'omit', headers: { 'X-Palabras': 'wrong' } })
  assert.equal(requests[3].init.headers.get('X-Palabras'), '1')
  assert.equal(requests[3].init.credentials, 'include')
})

test('401 invalidates session for every API path; 403 maps error without invalidation', async t => {
  let invalidations = 0
  const stop = onUnauthorized(() => { invalidations++ })
  try {
    for (const [status, kind] of [[401, 'unauthorized'], [403, 'forbidden'], [400, 'http']]) {
      t.mock.method(globalThis, 'fetch', async () => Response.json({ error: kind }, { status }))
      await assert.rejects(apiRequest('/api/future-feature'), error => error instanceof ApiError && error.kind === kind && error.status === status && error.body.error === kind)
      assert.equal(invalidations, 1)
    }
    t.mock.method(globalThis, 'fetch', async () => new Response('not JSON', { status: 401 }))
    await assert.rejects(me(), error => error instanceof ApiError && error.kind === 'unauthorized')
    assert.equal(invalidations, 2)
  } finally { stop() }
})

test('network and invalid successful JSON/session responses become typed errors', async t => {
  t.mock.method(globalThis, 'fetch', async () => { throw new TypeError('Failed to fetch') })
  await assert.rejects(me(), error => error instanceof ApiError && error.kind === 'network')
  t.mock.method(globalThis, 'fetch', async () => new Response('not JSON'))
  await assert.rejects(me(), error => error instanceof ApiError && error.kind === 'invalid-response')
  t.mock.method(globalThis, 'fetch', async () => Response.json({ username: 'ana', roles: [1] }))
  await assert.rejects(me(), error => error instanceof ApiError && error.kind === 'invalid-response')
})

test('learner routes allow either role; reviewer routes deny learner and send anonymous to login', () => {
  const learner = { username: 'alumno', roles: ['learner'] }
  const reviewer = { username: 'ana', roles: ['reviewer'] }
  for (const path of ['/', '/sesion', '/palabras', '/ajustes', '/debug/viewport', '/unknown']) {
    assert.equal(routeAccess(path, null), 'login')
    for (const user of [learner, reviewer]) assert.equal(routeAccess(path, user), 'allow')
  }
  for (const path of ['/revisar', '/aprobadas']) {
    assert.equal(routeAccess(path, null), 'login')
    assert.equal(routeAccess(path, learner), 'forbidden')
    assert.equal(routeAccess(path, reviewer), 'allow')
  }
  assert.equal(routeAccess('/login', null), 'allow')
})

test('login keeps intended local route and rejects external or recursive destinations', () => {
  assert.equal(loginDestination('?next=%2Frevisar'), '/revisar')
  assert.equal(loginDestination('?next=%2Fpalabras'), '/palabras')
  for (const next of ['//evil.test', '/\\evil.test', 'https://evil.test', '/login', '/login?next=/login']) assert.equal(loginDestination(`?next=${encodeURIComponent(next)}`), '/')
  assert.equal(loginDestination(''), '/')
})
