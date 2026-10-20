import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'
import { ApiError } from '../src/api/client.ts'
import { createPushQueue } from '../src/push/outbox.ts'
import { createPushClient } from '../src/push/pushClient.ts'

const tick = () => new Promise(resolve => setImmediate(resolve))
function load(file, imports, globals = {}) {
  const exports = {}
  const source = ts.transpileModule(readFileSync(new URL(file, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText
  runInNewContext(source, { exports, require: name => imports[name], Error, ...globals })
  return exports
}
// Execute real hook callbacks/subscriptions with controlled React storage, not DOM proof.
function harness() {
  const calls = []; const stored = new Map(); let optedOut = false; let sequence = 0; let subscription; let unsubscriptions = 0
  const bytes = new Uint8Array([4, ...Array.from({ length: 64 }, (_, i) => i)])
  const make = () => ({ endpoint: 'device-' + sequence, options: { applicationServerKey: bytes }, toJSON: () => ({ endpoint: 'device-' + sequence, keys: { p256dh: 'p', auth: 'a' } }), unsubscribe: async () => { unsubscriptions++; subscription = null; return true } })
  subscription = make()
  const env = { supported: () => true, permission: () => 'granted', requestPermission: async () => { throw new Error('Unexpected prompt') }, registration: async () => ({ pushManager: { getSubscription: async () => subscription, subscribe: async () => { sequence++; subscription = make(); return subscription } } }), optedOut: async () => optedOut, setOptOut: async v => { optedOut = v }, lock: async work => work() }
  const items = [7, 9].map(id => ({ id, deviceLabel: null, state: 'active', lastStatus: null, createdAtMs: 1, updatedAtMs: 1, isCurrent: false }))
  const queue = createPushQueue({ list: async () => structuredClone([...stored.values()]), put: async r => stored.set(r.id, structuredClone(r)), remove: async id => stored.delete(id) }, { online: () => true, send: async row => {
    calls.push(row.kind)
    if (row.kind === 'subscribe') { const id = row.body.endpoint === 'device-0' ? 7 : 8; if (!items.some(r => r.id === id)) items.push({ ...items[0], id, state: 'active' }); return { id, state: 'active' } }
    if (row.kind === 'revoke') items.find(r => r.id === row.body.id).state = 'rejected'
  } })
  const slots = []; let index = 0; const effects = []; const cleanups = []
  const memo = (fn, deps) => { const n = index++; if (!slots[n] || deps.some((d, i) => d !== slots[n].deps[i])) slots[n] = { deps, value: fn() }; return slots[n].value }
  const react = {
    useState(initial) { const n = index++; if (!(n in slots)) slots[n] = typeof initial === 'function' ? initial() : initial; return [slots[n], value => { slots[n] = typeof value === 'function' ? value(slots[n]) : value }] },
    useRef(initial) { return memo(() => ({ current: initial }), []) }, useMemo: memo, useCallback: (fn, deps) => memo(() => fn, deps),
    useEffect(fn, deps) { memo(() => { effects.push(fn) }, deps) }, useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
  }
  const { useReminder } = load('../src/push/useReminder.ts', { react, './api': {
    getAvailability: async () => ({ kind: 'available', config: { publicKey: Buffer.from(bytes).toString('base64url'), keyId: 'k' } }),
    getReminder: async () => ({ enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 2 }),
    getDevices: async currentId => { calls.push('list:' + currentId); return { items: structuredClone(items).map(r => ({ ...r, isCurrent: r.id === currentId })), truncated: false } },
  }, './outbox': { getPushQueue: () => queue }, './pushClient': { createPushClient: enqueue => createPushClient(enqueue, env) }, '../api/learn': { getDayStatus: async () => ({ status: null }) } }, { window: { addEventListener() {}, removeEventListener() {} }, document: { addEventListener() {}, removeEventListener() {} }, navigator: {} })
  const render = () => { index = 0; const model = useReminder('owner'); for (const fn of effects.splice(0)) cleanups.push(fn()); return model }
  const settle = async () => { for (let i = 0; i < 4; i++) { await tick(); await queue.flush() } return render() }
  render()
  return { render, settle, queue, calls, unsubscriptions: () => unsubscriptions, close() { for (const cleanup of cleanups) cleanup?.() } }
}
test('hook refetches after bodyless per-device acknowledgement with durable PUT id; remote does not unsubscribe', async () => {
  const h = harness()
  try {
    let m = await h.settle(); assert.equal(m.devices.items.find(r => r.id === 7).isCurrent, true)
    await m.revoke(m.devices.items.find(r => r.id === 9)); m = await h.settle()
    assert.equal(m.devices.items.find(r => r.id === 9).state, 'rejected')
    assert.equal(h.unsubscriptions(), 0)
    assert.ok(h.calls.slice(h.calls.indexOf('revoke') + 1).includes('list:7'))
  } finally { h.close() }
})
test('hook current revoke unsubscribes locally; explicit new endpoint changes currentId and preserves audit row', async () => {
  const h = harness()
  try {
    let m = await h.settle(); await m.revoke(m.devices.items.find(r => r.isCurrent)); m = await h.settle()
    assert.equal(h.unsubscriptions(), 1); assert.equal(m.device.state, 'opted-out')
    await m.client.sync(m.availability, true); m = await h.settle()
    assert.equal(m.outbox.currentId, 8)
    assert.equal(m.devices.items.find(r => r.id === 8).isCurrent, true)
    assert.equal(m.devices.items.find(r => r.id === 7).state, 'rejected')
    assert.equal(m.devices.items.find(r => r.id === 7).isCurrent, false)
  } finally { h.close() }
})
test('real sendMutation persists silent 409 revoked opt-out under device lock', async () => {
  let flag = false; const calls = []
  const { sendMutation } = load('../src/push/outbox.ts', { '../api/client.ts': { ApiError, apiRequest: async () => { calls.push('PUT'); throw new ApiError('http', 409, { error: 'revoked' }) } }, './storage.ts': { deviceLock: 'device', readOptOut: async () => flag, writeOptOut: async v => { calls.push('flag'); flag = v } } }, { navigator: { locks: { request: async (_name, work) => { calls.push('lock'); return work() } } } })
  const mutation = { kind: 'subscribe', body: { endpoint: 'e', keys: { p256dh: 'p', auth: 'a' }, keyId: 'k' } }
  await assert.rejects(sendMutation(mutation), e => e.status === 409)
  assert.equal(flag, true); assert.deepEqual(calls, ['lock', 'PUT', 'flag'])
  await assert.rejects(sendMutation(mutation), e => e.body.error === 'device_opted_out')
  assert.deepEqual(calls, ['lock', 'PUT', 'flag', 'lock'])
})
