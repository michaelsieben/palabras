import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'
import { ApiError } from '../src/api/client.ts'
import * as api from '../src/push/api.ts'
import { permissionCopy } from '../src/push/pushClient.ts'

// Real TSX handlers and virtual tree, not a rendered browser or device proof.
function component(file, apiOverrides = {}) {
  const state = []; let i = 0
  const jsx = (type, props) => ({ type, props })
  const react = { useState(initial) { const n = i++; if (!(n in state)) state[n] = typeof initial === 'function' ? initial() : initial; return [state[n], value => { state[n] = value }] }, useEffect() {} }
  const source = ts.transpileModule(readFileSync(new URL(file, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText
  const exports = {}
  runInNewContext(source, { exports, require: name => ({ react, 'react/jsx-runtime': { jsx, jsxs: jsx }, '../api/client': { ApiError }, './api': { ...api, ...apiOverrides }, './pushClient': { permissionCopy } })[name], Date, Math, FormData, Error, navigator: { userAgent: 'Local Chrome' } })
  return fn => { i = 0; return fn(exports) }
}
function nodes(t) { return Array.isArray(t) ? t.flatMap(nodes) : t && typeof t === 'object' ? [t, ...nodes(t.props?.children)] : [] }
function text(t) { return Array.isArray(t) ? t.map(text).join('') : t && typeof t === 'object' ? text(t.props?.children) : t == null || t === false ? '' : String(t) }
const button = (tree, label) => nodes(tree).find(n => n.type === 'button' && text(n) === label)
const tick = () => new Promise(resolve => setImmediate(resolve))
function model(state = 'subscribed', availability = { kind: 'available', config: { publicKey: 'k', keyId: 'id' } }) {
  return { reminder: { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 0 }, availability, device: { state, endpoint: state === 'subscribed' ? 'https://push.test/device' : null }, devices: { items: deviceRows(), truncated: false }, devicesError: '', error: '', outbox: { pending: 0, rejected: 0, error: false, lastState: '', reason: '' }, client: { sync: async () => {}, optOut: async () => {} }, queue: { enqueue: async () => {}, trigger() {} }, refresh: async () => {}, revoke: async () => {} }
}
function deviceRows() { return ['active', 'stale_key', 'rejected'].map((state, i) => ({ id: i + 1, deviceLabel: null, state, lastStatus: null, createdAtMs: 1, updatedAtMs: 1, isCurrent: i === 0 })) }
for (const reason of ['push_disabled', 'push_unavailable']) test(`503 ${reason}: no disponible, never zero devices; preferences remain accessible`, () => {
  const render = component('../src/push/ReminderPanel.tsx'); const m = model('subscribed', { kind: 'unavailable', reason })
  const tree = render(e => e.ReminderPanel({ model: m, navigate() {} }))
  assert.match(text(tree), /Recordatorios no disponibles/)
  assert.doesNotMatch(text(tree), /0 dispositivos pueden/)
  assert.equal(button(tree, 'Enviar prueba').props.disabled, true)
  assert.equal(button(tree, 'Sincronizar este dispositivo'), undefined)
  assert.ok(button(tree, 'Guardar preferencias'))
  assert.equal(nodes(tree).filter(n => n.type === 'button' && text(n) === 'Desactivar').length, 0)
  assert.match(text(tree), /Activo con la clave actual.*Necesita volver a suscribirse.*Desactivado/)
  assert.doesNotMatch(text(tree), /Reintentar envío/)
})
for (const state of ['unsupported', 'default', 'denied', 'granted-unsubscribed', 'subscribed', 'opted-out']) test(`UI permission ${state} presents honest explanation and action`, () => {
  const render = component('../src/push/ReminderPanel.tsx'); const m = model(state)
  const tree = render(e => e.ReminderPanel({ model: m, navigate() {} }))
  assert.ok(text(tree).includes(permissionCopy[state]))
  if (['unsupported', 'denied'].includes(state)) assert.equal(button(tree, 'Activar recordatorio'), undefined)
  else assert.ok(button(tree, state === 'subscribed' ? 'Sincronizar este dispositivo' : 'Activar recordatorio'))
  if (state === 'denied') assert.match(text(tree), /Información del sitio.*Notificaciones.*Permitir/)
})
test('test button gets 429 cooldown, no success claim; 503 receives honest no-disponible message', async () => {
  let status = 429
  const render = component('../src/push/ReminderPanel.tsx', { sendTest: async () => { throw new ApiError('http', status, {}, '45') } })
  const m = model(); const view = () => render(e => e.ReminderPanel({ model: m, navigate() {} }))
  button(view(), 'Enviar prueba').props.onClick(); await tick()
  assert.match(text(view()), /Demasiadas pruebas/)
  assert.equal(button(view(), 'Reintentar prueba en 45 s').props.disabled, true)
  status = 503
  // Invoke original handler directly to exercise server 503 even before a config refresh.
  const second = component('../src/push/ReminderPanel.tsx', { sendTest: async () => { throw new ApiError('http', 503) } })
  const view2 = () => second(e => e.ReminderPanel({ model: m, navigate() {} }))
  button(view2(), 'Enviar prueba').props.onClick(); await tick()
  assert.match(text(view2()), /Prueba no disponible/)
})
test('mute sheet queues each exclusive shortcut; cancel one write, back/change-time no mutation', async () => {
  const writes = []; const navigations = []; const m = model()
  m.queue.enqueue = async mutation => writes.push(mutation)
  const render = component('../src/push/MuteSheet.tsx'); const view = () => render(e => e.MuteSheet({ model: m, navigate: p => navigations.push(p) }))
  for (const [label, choice] of [['Hasta mañana (mañana incluido)', 'tomorrow'], ['Pausar 3 días', '3d'], ['Pausar 7 días', '7d'], ['Desactivar en toda la cuenta', 'disable'], ['Cancelar silencio y reanudar', 'cancel']]) {
    button(view(), label).props.onClick(); await tick()
    assert.deepEqual(JSON.parse(JSON.stringify(writes.at(-1))), { kind: 'reminder', body: { muteChoice: choice } })
  }
  button(view(), 'Cambiar hora').props.onClick(); button(view(), 'Volver sin cambios').props.onClick()
  assert.equal(writes.length, 5); assert.deepEqual(navigations, ['/ajustes', '/ajustes'])
  assert.match(text(view()), /Silenciar no salta el día/)
})
test('device list renders all three states, current-only UA fallback and honest first-50 note', () => {
  const render = component('../src/push/ReminderPanel.tsx'); const m = model()
  m.devices.truncated = true
  const tree = render(e => e.ReminderPanel({ model: m, navigate() {} }))
  const rows = nodes(tree).filter(n => n.type === 'li')
  assert.match(text(rows[0]), /Local Chrome — Este dispositivo.*Activo/)
  assert.match(text(rows[1]), /DispositivoNecesita volver a suscribirse/)
  assert.doesNotMatch(text(rows[1]), /Chrome|Activo/)
  assert.match(text(rows[2]), /DispositivoDesactivado/)
  assert.match(text(tree), /Se muestran los primeros 50.*Hay más/)
  m.devices.truncated = false
  assert.doesNotMatch(text(render(e => e.ReminderPanel({ model: m, navigate() {} }))), /primeros 50/)
})
test('per-row revoke delegates exact row then refreshes; failures stay visible with row intact', async () => {
  const calls = []; const render = component('../src/push/ReminderPanel.tsx'); const m = model()
  m.revoke = async row => { calls.push(row.id); throw new Error('No se pudo guardar la baja') }
  m.refresh = async () => calls.push('refresh')
  const view = () => render(e => e.ReminderPanel({ model: m, navigate() {} }))
  button(view(), 'Desactivar').props.onClick(); await tick()
  assert.deepEqual(calls, [1]); assert.match(text(view()), /No se pudo guardar la baja/)
  assert.match(text(view()), /Activo con la clave actual/)
  m.revoke = async row => calls.push(row.id)
  button(view(), 'Desactivar').props.onClick(); await tick()
  assert.deepEqual(calls, [1, 1, 'refresh']); assert.match(text(view()), /pendiente de confirmación/)
})
test('silent revoked maps to off with explicit CTA, never an error toast', () => {
  const render = component('../src/push/ReminderPanel.tsx'); const m = model('opted-out')
  m.outbox.lastState = 'revoked'; m.outbox.reason = 'revoked'
  const tree = render(e => e.ReminderPanel({ model: m, navigate() {} }))
  assert.match(text(tree), /No se reactivarán al abrir la app/)
  assert.ok(button(tree, 'Activar recordatorio'))
  assert.equal(nodes(tree).filter(n => n.props?.role === 'alert').length, 0)
})
test('503 empty stored list still never claims zero devices', () => {
  const render = component('../src/push/ReminderPanel.tsx'); const m = model('subscribed', { kind: 'unavailable', reason: 'push_disabled' })
  m.devices.items = []
  const tree = render(e => e.ReminderPanel({ model: m, navigate() {} }))
  assert.doesNotMatch(text(tree), /No hay suscripciones|0 dispositivos/)
})
