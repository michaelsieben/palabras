import test from 'node:test'
import assert from 'node:assert/strict'
import { createContext, runInContext } from 'node:vm'
import { readFile } from 'node:fs/promises'
import { workerSource } from '../scripts/pwa.mjs'

const publicKey = Buffer.from([4, ...Array.from({ length: 64 }, (_, i) => i)]).toString('base64url')
const keyBytes = Buffer.from(publicKey, 'base64url')
const subscription = (endpoint = 'https://push.test/new', key = keyBytes) => ({
  endpoint, options: { applicationServerKey: key },
  toJSON: () => ({ endpoint, keys: { p256dh: 'device-key', auth: 'device-auth' } }),
  unsubscribe: async () => true,
})
const daily = { v: 1, kind: 'daily', localDate: '2026-10-20', expiresAt: '2099-10-20T21:30:00Z', streak: 12, skipAvailable: true, url: '/?start=1' }
const json = (body, status = 200) => new Response(JSON.stringify(body), { status })
function harness({ maxActions = 2, fetcher, windows = [], subscribeError, displayError, source = workerSource('push-test', []) } = {}) {
  const events = {}; const notifications = []; const requests = []; const opened = []; const subscribed = []
  const context = createContext({
    URL, Response, Uint8Array, atob, Notification: { maxActions },
    self: {
      location: { origin: 'https://palabras.test' },
      addEventListener: (name, handler) => { events[name] = handler },
      registration: {
        showNotification: async (title, options) => { if (displayError) throw new Error('denied'); notifications.push({ title, ...structuredClone(options) }) },
        pushManager: { subscribe: async options => { subscribed.push(options); if (subscribeError) throw new Error('denied'); return subscription() } },
      },
      clients: {
        matchAll: async options => { assert.equal(options.type, 'window'); assert.equal(options.includeUncontrolled, true); return windows },
        openWindow: async url => { opened.push(url) },
      },
    },
    fetch: async (url, options) => {
      requests.push({ url, ...structuredClone(options) })
      assert.equal(options.credentials, 'same-origin'); assert.equal(options.cache, 'no-store')
      if (options.method) { assert.equal(options.headers['X-Palabras'], '1'); assert.equal(options.headers['Content-Type'], 'application/json') }
      if (fetcher) return fetcher(url, options)
      if (url === '/api/push/config') return json({ publicKey, keyId: 'key-id' })
      if (url === '/api/learn/reminder') return json({ enabled: true })
      return json({})
    },
  })
  runInContext(source, context)
  const dispatch = async (name, values) => {
    let pending
    events[name]({ ...values, waitUntil: promise => { pending = promise } })
    assert.ok(pending, 'event extends worker lifetime'); await pending
  }
  const push = payload => dispatch('push', { data: { json: () => payload } })
  let closed = 0
  const click = (action = '', data = { localDate: daily.localDate, url: daily.url }) => dispatch('notificationclick', { action, notification: { data, close: () => { closed++ } } })
  return { dispatch, push, click, notifications, requests, opened, subscribed, closed: () => closed }
}

test('actual built worker executes all three push handlers without bundler-only dependencies', async () => {
  const source = await readFile(new URL('../dist/sw.js', import.meta.url), 'utf8')
  const h = harness({ source })
  await h.push(daily); await h.click(); await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription() })
  assert.equal(h.notifications.length, 1); assert.equal(h.opened.length, 1)
  assert.equal(JSON.parse(h.requests.at(-1).body).reactivate, true)
})

test('daily push renders factual Spanish copy, stable tag, silent replacement and deep-link data', async () => {
  const h = harness()
  await h.push(daily); await h.push(daily)
  assert.equal(h.notifications.length, 2)
  for (const notification of h.notifications) {
    assert.equal(notification.title, 'Tu repaso de hoy'); assert.equal(notification.body, 'Racha 12 · salto disponible')
    assert.equal(notification.tag, 'daily'); assert.equal(notification.renotify, false)
    assert.equal(notification.data.url, 'https://palabras.test/?start=1')
    assert.deepEqual(notification.actions.map(action => action.action), ['skip', 'mute'])
  }
})

test('backend-supplied title/body are preserved; test and malformed payloads still display', async () => {
  const h = harness()
  await h.push({ ...daily, title: 'Backend title', body: 'Backend body' })
  assert.equal(h.notifications[0].title, 'Backend title'); assert.equal(h.notifications[0].body, 'Backend body')
  await h.push({ v: 1, kind: 'test' })
  assert.equal(h.notifications[1].title, 'Prueba de recordatorio ✓'); assert.equal(h.notifications[1].tag, 'test')
  for (const payload of [null, [], 'bad']) await h.push(payload)
  await h.dispatch('push', { data: { json: () => { throw new Error('invalid JSON') } } })
  await h.dispatch('push', {})
  assert.equal(h.notifications.length, 7)
  assert.equal(h.notifications.at(-1).title, 'Tu repaso de hoy')
})

test('skip-used copy has only mute; platform maxActions trims including zero/unknown', async () => {
  const h = harness()
  await h.push({ ...daily, skipAvailable: false, skipNextAvailable: '2026-10-23' })
  assert.match(h.notifications[0].body, /Racha 12 · salto usado — vuelve el/)
  assert.deepEqual(h.notifications[0].actions.map(action => action.action), ['mute'])
  for (const maxActions of [0, 1, 2, null]) {
    const trimmed = harness({ maxActions }); await trimmed.push(daily)
    assert.equal(trimmed.notifications[0].actions.length, maxActions ?? 0)
  }
})

test('expired/missing/invalid expiry is silent, no actions, still user visible', async () => {
  const h = harness()
  for (const expiresAt of ['2000-01-01T00:00:00Z', undefined, 'bad']) {
    await h.push({ ...daily, expiresAt })
    const notification = h.notifications.at(-1)
    assert.equal(notification.title, 'Repaso pendiente'); assert.equal(notification.silent, true)
    assert.deepEqual(notification.actions, []); assert.equal(notification.body, '')
  }
})

test('body click closes, focuses existing same-origin window, navigates target and audits open', async () => {
  const calls = []
  const h = harness({ windows: [
    { url: 'https://other.test/', focus: () => assert.fail('external client') },
    { url: 'https://palabras.test/ajustes', focus: async () => { calls.push('focus') }, navigate: async url => { calls.push(url); return {} } },
  ] })
  await h.click()
  assert.equal(h.closed(), 1); assert.deepEqual(calls, ['focus', 'https://palabras.test/?start=1']); assert.deepEqual(h.opened, [])
  const audit = h.requests.find(request => request.url.endsWith('/notification-action'))
  assert.equal(audit.method, 'POST'); assert.deepEqual(JSON.parse(audit.body), { localDate: daily.localDate, action: 'open', result: 'ok' })
})

test('mute opens dated sheet; closing client falls back to new window', async () => {
  const h = harness({ windows: [{ url: 'https://palabras.test/', focus: async () => { throw new Error('closed') } }] })
  await h.click('mute')
  assert.deepEqual(h.opened, ['https://palabras.test/ajustes/recordatorio?from=notif&d=2026-10-20'])
  assert.equal(JSON.parse(h.requests.at(-1).body).action, 'mute')
})

test('signed-out body/mute clicks reach login with escaped next, no auth rejection storm', async () => {
  for (const action of ['', 'mute']) {
    const h = harness({ fetcher: async url => url === '/api/auth/me' ? json({}, 401) : json({}) })
    await h.click(action)
    const url = new URL(h.opened[0]); assert.equal(url.pathname, '/login')
    assert.equal(url.searchParams.get('next'), action ? '/ajustes/recordatorio?from=notif&d=2026-10-20' : '/?start=1')
    assert.equal(JSON.parse(h.requests.at(-1).body).result, 'unauthenticated')
  }
})

test('untrusted deep links stay same-origin and outside API, offline navigation still opens once', async () => {
  for (const url of ['https://evil.test/', '//evil.test/', '/api/push/test', '/api', undefined]) {
    const h = harness({ fetcher: async () => { throw new Error('offline') } })
    await h.click('', { url })
    assert.deepEqual(h.opened, ['https://palabras.test/?start=1'])
    assert.equal(h.requests.length, 1, 'no date, no audit')
  }
})

for (const [status, body, title, result] of [
  [200, {}, 'Saltado ✓ — hasta mañana', 'ok'],
  [409, { reason: 'skip_unavailable' }, 'Sin salto disponible — ¿5 min?', 'skip_unavailable'],
  [409, { error: 'already_done' }, 'Ya hecho hoy ✓', 'already_done'],
  [400, {}, 'Ese día ya pasó', 'error'],
  [500, {}, 'No se pudo saltar — abre Palabras', 'error'],
]) test(`skip ${status}/${result} shows outcome and audits without opening app`, async () => {
  const h = harness({ fetcher: async url => url.endsWith('/day/skip') ? json(body, status) : json({}) })
  await h.click('skip')
  const skip = h.requests[0]
  assert.equal(skip.url, '/api/learn/day/skip'); assert.equal(skip.method, 'POST')
  assert.deepEqual(JSON.parse(skip.body), { localDate: daily.localDate, source: 'notification' })
  assert.equal(h.notifications[0].title, title); assert.equal(h.notifications[0].tag, 'daily')
  assert.equal(JSON.parse(h.requests.at(-1).body).result, result); assert.deepEqual(h.opened, [])
})

test('skip 401 opens login next=/ajustes and records unauthenticated', async () => {
  const h = harness({ fetcher: async url => json({}, url === '/api/auth/me' || url.endsWith('/day/skip') ? 401 : 200) })
  await h.click('skip')
  assert.deepEqual(h.opened, ['https://palabras.test/login?next=/ajustes'])
  assert.deepEqual(h.notifications, []); assert.equal(JSON.parse(h.requests.at(-1).body).result, 'unauthenticated')
})

test('subscription change decodes VAPID key, re-subscribes and PUTs reactivate:true with replaced endpoint', async () => {
  const h = harness()
  await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription('https://push.test/old') })
  assert.equal(h.subscribed.length, 1); assert.equal(h.subscribed[0].userVisibleOnly, true)
  assert.deepEqual([...h.subscribed[0].applicationServerKey], [...keyBytes])
  const write = h.requests.at(-1); assert.equal(write.url, '/api/push/subscription'); assert.equal(write.method, 'PUT')
  assert.deepEqual(JSON.parse(write.body), {
    endpoint: 'https://push.test/new', keys: { p256dh: 'device-key', auth: 'device-auth' },
    keyId: 'key-id', replacesEndpoint: 'https://push.test/old', reactivate: true,
  })
})

test('subscription change uses matching browser-provided subscription, rotates mismatched key', async () => {
  const h = harness()
  await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription('https://push.test/old'), newSubscription: subscription() })
  assert.deepEqual(h.subscribed, []); assert.equal(JSON.parse(h.requests.at(-1).body).reactivate, true)
  let unsubscribed = false
  const rotated = { ...subscription('https://push.test/rotated', new Uint8Array([1])), unsubscribe: async () => { unsubscribed = true } }
  await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription(), newSubscription: rotated })
  assert.equal(unsubscribed, true); assert.equal(h.subscribed.length, 1)
})

test('signed-out, reviewer, push-off config responses are quiet no-ops with no subscription write', async () => {
  for (const status of [401, 403, 503]) {
    const h = harness({ fetcher: async () => json({}, status) })
    await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription() })
    assert.equal(h.requests.length, 1); assert.deepEqual(h.subscribed, [])
  }
})

test('disabled reminders, signed-out settings and empty change cannot create subscriptions', async () => {
  for (const status of [200, 401]) {
    const h = harness({ fetcher: async url => url.endsWith('/config') ? json({ publicKey, keyId: 'key-id' }) : json({ enabled: false }, status) })
    await h.dispatch('pushsubscriptionchange', { oldSubscription: subscription() })
    assert.equal(h.requests.length, 2); assert.deepEqual(h.subscribed, [])
  }
  const h = harness(); await h.dispatch('pushsubscriptionchange', {})
  assert.deepEqual(h.requests, []); assert.deepEqual(h.subscribed, [])
})

test('offline, subscribe failure, backend resync refusal and revoked display never reject event lifetime', async () => {
  const offline = harness({ fetcher: async () => { throw new Error('offline') } })
  await offline.dispatch('pushsubscriptionchange', { oldSubscription: subscription() }); await offline.click('skip')
  assert.equal(offline.requests.length, 3, 'one config, one skip, one best-effort audit; no retries')
  const denied = harness({ subscribeError: true }); await denied.dispatch('pushsubscriptionchange', { oldSubscription: subscription() })
  assert.equal(denied.requests.length, 2)
  const rejected = harness({ fetcher: async url => url.endsWith('/config') ? json({ publicKey, keyId: 'key-id' }) : url.endsWith('/reminder') ? json({ enabled: true }) : json({ error: 'revoked' }, 409) })
  await rejected.dispatch('pushsubscriptionchange', { oldSubscription: subscription() })
  assert.equal(rejected.requests.length, 3)
  await harness({ displayError: true }).push(daily)
})
