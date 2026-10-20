import test from 'node:test'
import assert from 'node:assert/strict'
import { ApiError, apiRequest } from '../src/api/client.ts'
import { getAvailability, getDevices, parseDevices, parseReminder, retryDeadline, sendTest, windowCopy } from '../src/push/api.ts'
import { createPushClient, permissionState } from '../src/push/pushClient.ts'
import { createPushQueue, sendMutation } from '../src/push/outbox.ts'
import { consumeStart, loginDestination, routeAccess } from '../src/routeAccess.ts'

const bytes = new Uint8Array([4, ...Array.from({ length: 64 }, (_, i) => i)])
const available = { kind: 'available', config: { publicKey: Buffer.from(bytes).toString('base64url'), keyId: 'key-id' } }
function environment({ supported = true, permission = 'granted', subscribed = false, optedOut = false, key = bytes } = {}) {
  const calls = []; let current
  const subscription = () => ({ endpoint: 'https://push.test/device', options: { applicationServerKey: key }, toJSON: () => ({ endpoint: 'https://push.test/device', keys: { p256dh: 'p', auth: 'a' } }), unsubscribe: async () => { calls.push('unsubscribe'); current = null; return true } })
  current = subscribed ? subscription() : null
  const env = {
    supported: () => supported, permission: () => permission,
    requestPermission: async () => { calls.push('prompt'); permission = 'granted'; return permission },
    registration: async () => ({ pushManager: { getSubscription: async () => current, subscribe: async options => { calls.push('subscribe'); assert.deepEqual(options.applicationServerKey, bytes); current = subscription(); return current } } }),
    optedOut: async () => optedOut, setOptOut: async v => { calls.push('flag:' + v); optedOut = v }, lock: async work => work(),
  }
  const writes = []; const client = createPushClient(async m => { calls.push('enqueue'); writes.push(m) }, env)
  return { env, client, writes, calls }
}
for (const [supported, permission, subscribed, optedOut, expected] of [
  [false, 'default', false, false, 'unsupported'], [true, 'default', false, false, 'default'], [true, 'denied', false, false, 'denied'], [true, 'granted', false, false, 'granted-unsubscribed'], [true, 'granted', true, false, 'subscribed'], [true, 'granted', false, true, 'opted-out'],
]) test(`permission state: ${expected}; inspecting never prompts`, async () => {
  assert.equal(permissionState(supported, permission, subscribed, optedOut), expected)
  const h = environment({ supported, permission, subscribed, optedOut })
  assert.equal((await h.client.inspect()).state, expected)
  assert.deepEqual(h.calls, [])
})
test('default load never prompts/subscribes; explicit activation prompts once and sends reactivate:true', async () => {
  const h = environment({ permission: 'default' })
  await h.client.sync(available)
  assert.deepEqual(h.calls, [])
  await h.client.sync(available, true)
  assert.deepEqual(h.calls, ['prompt', 'flag:false', 'subscribe', 'enqueue'])
  assert.equal(h.writes[0].body.reactivate, true)
})
test('granted silent app-open resync heals missing subscription, never sends reactivate', async () => {
  const h = environment()
  assert.equal((await h.client.sync(available)).state, 'subscribed')
  assert.equal(h.writes[0].body.reactivate, undefined)
  assert.equal(h.calls.includes('prompt'), false)
})
test('key rotation unsubscribes old browser key before replacing it; explicit resync reactivates', async () => {
  const h = environment({ subscribed: true, key: new Uint8Array([1]) })
  await h.client.sync(available, true)
  assert.deepEqual(h.calls, ['flag:false', 'unsubscribe', 'subscribe', 'enqueue'])
  assert.equal(h.writes[0].body.reactivate, true)
})
test('revoked browser permission queues cleanup but never prompts or creates another subscription', async () => {
  const h = environment({ permission: 'denied', subscribed: true })
  await h.client.sync(available)
  assert.deepEqual(h.calls, ['enqueue', 'unsubscribe'])
  assert.equal(h.writes[0].kind, 'unsubscribe')
})
test('device opt-out persists before local unsubscribe and queued deletion; passive reload honours flag', async () => {
  const h = environment({ subscribed: true })
  assert.equal((await h.client.optOut()).state, 'opted-out')
  assert.deepEqual(h.calls, ['flag:true', 'enqueue', 'unsubscribe'])
  await createPushClient(async m => h.writes.push(m), h.env).sync(available)
  assert.equal(h.writes.length, 1)
  await h.client.sync(available, true)
  assert.equal(h.writes[1].body.reactivate, true)
})
test('push-off, key fault, unknown and unsupported never subscribe even on explicit action', async () => {
  for (const a of [{ kind: 'unavailable', reason: 'push_disabled' }, { kind: 'unavailable', reason: 'push_unavailable' }, { kind: 'unknown' }]) {
    const h = environment({ permission: 'default' }); await h.client.sync(a, true); assert.deepEqual(h.calls, [])
  }
  const h = environment({ supported: false }); await h.client.sync(available, true); assert.deepEqual(h.calls, [])
})
test('config 503 discriminates push-disabled/key-fault from available + zero and preserves stored settings', async t => {
  const reminder = { enabled: false, time: '21:30', mutedUntil: '2026-10-22', windowMin: 120, activeDevices: 0 }
  assert.deepEqual(parseReminder(reminder), reminder)
  for (const reason of ['push_disabled', 'push_unavailable']) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ error: reason }, { status: 503 }))
    assert.deepEqual(await getAvailability(), { kind: 'unavailable', reason })
  }
  t.mock.method(globalThis, 'fetch', async () => Response.json(available.config))
  assert.deepEqual(await getAvailability(), available)
  t.mock.method(globalThis, 'fetch', async () => { throw new Error('offline') })
  assert.deepEqual(await getAvailability(), { kind: 'unknown' })
})
test('reminder contract rejects bad time/window/count and exposes capped window without a late ring promise', () => {
  const r = { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 1 }
  for (const patch of [{ time: '22:60' }, { time: '23:50' }, { windowMin: 0 }, { activeDevices: -1 }]) assert.throws(() => parseReminder({ ...r, ...patch }), /invalid-response/)
  assert.match(windowCopy('22:00', 90), /22:00 y las 23:30/)
  assert.match(windowCopy('23:45', 90), /23:59/)
})
function store() { const rows = new Map(); return { list: async () => structuredClone([...rows.values()]), put: async row => rows.set(row.id, structuredClone(row)), remove: async id => rows.delete(id) } }
test('offline outbox survives recreation and keeps muteChoice:cancel a single exclusive write', async () => {
  const storage = store(); const sent = []
  const offline = createPushQueue(storage, { online: () => false }); offline.start()
  await offline.enqueue({ kind: 'reminder', body: { muteChoice: 'cancel' } }); await offline.flush(); offline.stop()
  assert.equal(offline.getSnapshot().pending, 1)
  const reload = createPushQueue(storage, { online: () => true, send: async r => { sent.push(r.body); return {} } }); reload.start(); await reload.flush()
  assert.deepEqual(sent, [{ muteChoice: 'cancel' }]); assert.equal(reload.getSnapshot().pending, 0)
})
test('outbox serializes settings and device cleanup, removes pending activation on opt-out', async () => {
  const storage = store(); const sent = []
  const q = createPushQueue(storage, { online: () => true, send: async r => { sent.push(r.kind); return {} } })
  await q.enqueue({ kind: 'subscribe', body: { endpoint: 'e', keys: { p256dh: 'p', auth: 'a' }, keyId: 'k', reactivate: true } })
  await q.enqueue({ kind: 'reminder', body: { enabled: false } })
  await q.enqueue({ kind: 'unsubscribe', body: { endpoint: 'e' } })
  q.start(); await q.flush()
  assert.deepEqual(sent, ['reminder', 'unsubscribe'])
})
test('durable replay follows sequence, not IndexedDB UUID key order', async () => {
  const storage = store(); const baseList = storage.list
  storage.list = async () => (await baseList()).sort((a, b) => b.order - a.order)
  const sent = []; const q = createPushQueue(storage, { online: () => true, send: async r => { sent.push(r.body); return {} } })
  await q.enqueue({ kind: 'reminder', body: { muteChoice: '7d' } })
  await q.enqueue({ kind: 'reminder', body: { muteChoice: 'cancel' } })
  q.start(); await q.flush()
  assert.deepEqual(sent, [{ muteChoice: '7d' }, { muteChoice: 'cancel' }])
})
test('storage read failure is visible, not a false zero pending success', async () => {
  const q = createPushQueue({ ...store(), list: async () => { throw new Error('storage unavailable') } }, { online: () => true })
  q.start(); await q.flush(); q.stop()
  assert.equal(q.getSnapshot().storageError, true)
})
test('409 revoked/stale_key reject visibly without retry/reactivation; network/503 stay durable', async () => {
  for (const [status, reason] of [[409, 'revoked'], [409, 'stale_key'], [503, 'push_disabled'], [undefined, 'network']]) {
    let calls = 0; const q = createPushQueue(store(), { online: () => true, send: async () => { calls++; throw new ApiError(status ? 'http' : 'network', status, { error: reason }) } })
    await q.enqueue({ kind: 'reminder', body: { enabled: false } }); q.start(); await q.flush(); q.stop()
    assert.equal(calls, 1); assert.equal(q.getSnapshot().rejected, status === 409 ? 1 : 0); assert.equal(q.getSnapshot().pending, status === 409 ? 0 : 1)
  }
})
test('test-send preserves Retry-After and reports transport acceptance rather than notification display', async t => {
  t.mock.method(globalThis, 'fetch', async () => Response.json({ error: 'rate_limited' }, { status: 429, headers: { 'Retry-After': '45' } }))
  await assert.rejects(apiRequest('/api/push/test', { method: 'POST', body: '{}' }), e => e.retryAfter === '45')
  assert.equal(retryDeadline('45', 1000), 46000)
  assert.equal(retryDeadline(new Date(61000).toUTCString(), 1000), 61000)
  assert.equal(retryDeadline(null, 1000), 31000)
  t.mock.method(globalThis, 'fetch', async () => Response.json({ results: [{ outcome: 'delivered' }] }))
  assert.match(await sendTest('https://push.test/device'), /Comprueba si llega/)
  t.mock.method(globalThis, 'fetch', async () => Response.json({ results: [{ outcome: 'rejected' }] }))
  assert.match(await sendTest('https://push.test/device'), /no se ha entregado/)
})
test('login preserves only known mute/start handoffs; start flag consumed once; reviewer cannot open mute route', () => {
  const owner = { username: 'o', role: 'owner' }; const reviewer = { username: 'r', role: 'reviewer' }
  for (const target of ['/?start=1', '/ajustes/recordatorio?from=notif&d=2026-10-20']) assert.equal(loginDestination('?next=' + encodeURIComponent(target), owner), target)
  for (const target of ['//evil.test/?start=1', '/login?next=/?start=1', '/ajustes/recordatorio?url=https://evil.test']) assert.equal(loginDestination('?next=' + encodeURIComponent(target), owner), '/')
  assert.equal(consumeStart('?start=1'), '/'); assert.equal(consumeStart(''), null)
  assert.equal(routeAccess('/ajustes/recordatorio', reviewer), 'forbidden')
})
test('list fetch uses only acknowledged currentId and validates three states/truncated', async t => {
  const list = { items: ['active', 'stale_key', 'rejected'].map((state, i) => ({ id: i + 1, deviceLabel: null, state, lastStatus: null, createdAtMs: 1, updatedAtMs: 2, isCurrent: i === 0 })), truncated: true }
  const urls = []
  t.mock.method(globalThis, 'fetch', async url => { urls.push(url); return Response.json(list) })
  assert.deepEqual(await getDevices(42), list)
  assert.deepEqual(await getDevices(null), list)
  assert.deepEqual(urls, ['/api/push/subscriptions?currentId=42', '/api/push/subscriptions'])
  assert.throws(() => parseDevices({ ...list, truncated: undefined }), /invalid-response/)
  assert.throws(() => parseDevices({ ...list, items: [{ ...list.items[0], state: 'revoked' }] }), /invalid-response/)
})
test('per-id revoke sends bodyless DELETE with CSRF header and accepts empty 204', async t => {
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    assert.equal(url, '/api/push/subscriptions/12'); assert.equal(init.method, 'DELETE')
    assert.equal(init.headers.get('X-Palabras'), '1'); assert.equal(init.body, undefined)
    return new Response(null, { status: 204 })
  })
  assert.equal(await sendMutation({ kind: 'revoke', body: { id: 12, current: false } }), undefined)
})
test('current per-id opt-out persists and queues before browser unsubscribe; remote revoke never touches browser', async () => {
  const h = environment({ subscribed: true }); await h.client.optOut(12)
  assert.deepEqual(h.calls, ['flag:true', 'enqueue', 'unsubscribe'])
  assert.deepEqual(h.writes, [{ kind: 'revoke', body: { id: 12, current: true } }])
  const q = createPushQueue(store(), { online: () => true, send: async () => undefined })
  await q.enqueue({ kind: 'revoke', body: { id: 13, current: false } }); q.start(); await q.flush(); q.stop()
  assert.deepEqual(h.calls, ['flag:true', 'enqueue', 'unsubscribe'])
})
test('acknowledged PUT id survives reload and subsequent revoke; resubscribe replaces identity with new row id', async () => {
  const storage = store(); let id = 7
  const q = createPushQueue(storage, { online: () => true, send: async row => row.kind === 'subscribe' ? { id, state: 'active' } : undefined })
  const subscribe = { kind: 'subscribe', body: { endpoint: 'e', keys: { p256dh: 'p', auth: 'a' }, keyId: 'k' } }
  await q.enqueue(subscribe); q.start(); await q.flush()
  assert.equal(q.getSnapshot().currentId, 7)
  await q.enqueue({ kind: 'revoke', body: { id: 7, current: true } }); await q.flush(); q.stop()
  const reloaded = createPushQueue(storage, { online: () => false }); reloaded.start(); await reloaded.flush(); reloaded.stop()
  assert.equal(reloaded.getSnapshot().currentId, 7)
  id = 8; await q.enqueue({ ...subscribe, body: { ...subscribe.body, endpoint: 'new', reactivate: true } }); q.start(); await q.flush(); q.stop()
  assert.equal(q.getSnapshot().currentId, 8); assert.equal(q.getSnapshot().lastState, 'active')
})
test('silent subscribe 409 revoked is off/explicit-click state, not rejected error and not retried', async () => {
  let calls = 0
  const q = createPushQueue(store(), { online: () => true, send: async () => { calls++; throw new ApiError('http', 409, { error: 'revoked' }) } })
  await q.enqueue({ kind: 'subscribe', body: { endpoint: 'e', keys: { p256dh: 'p', auth: 'a' }, keyId: 'k' } }); q.start(); await q.flush(); await q.flush(); q.stop()
  assert.equal(calls, 1); assert.equal(q.getSnapshot().lastState, 'revoked'); assert.equal(q.getSnapshot().rejected, 0); assert.equal(q.getSnapshot().pending, 0)
})
for (const reason of ['push_disabled', 'push_unavailable']) test(`revoke 503 ${reason} stops replay and maps server availability, not generic failure`, async () => {
  let calls = 0
  const q = createPushQueue(store(), { online: () => true, send: async () => { calls++; throw new ApiError('http', 503, { error: reason }) } })
  await q.enqueue({ kind: 'revoke', body: { id: 1, current: false } }); q.start(); await q.flush(); await q.flush(); q.stop()
  assert.equal(calls, 1); assert.equal(q.getSnapshot().unavailable, reason); assert.equal(q.getSnapshot().rejected, 0); assert.equal(q.getSnapshot().pending, 0)
})
test('failed revoke stays pending offline/network; 404 remains visible rather than looking successful', async () => {
  for (const status of [undefined, 404]) {
    const q = createPushQueue(store(), { online: () => true, send: async () => { throw new ApiError(status ? 'http' : 'network', status, { error: 'subscription_not_found' }) } })
    await q.enqueue({ kind: 'revoke', body: { id: 1, current: false } }); q.start(); await q.flush(); q.stop()
    assert.equal(q.getSnapshot().pending, status ? 0 : 1); assert.equal(q.getSnapshot().rejected, status ? 1 : 0)
    assert.notEqual(q.getSnapshot().lastState, 'revoked')
  }
})
