import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import { randomUUID } from 'node:crypto'
import ts from 'typescript'
import { ApiError } from '../src/api/client.ts'
import * as model from '../src/review/reviewModel.ts'
import { reviewWord, approvedWord, reviewAction } from './reviewFixtures.mjs'

// Existing SpeakButton harness pattern: real TSX handlers/virtual tree, not a browser or visual proof.
function component(file, modules = {}) {
  const state = []; const refs = []; let si = 0; let ri = 0; let effects = []
  const react = {
    useState(initial) { const i = si++; if (!(i in state)) state[i] = typeof initial === 'function' ? initial() : initial; return [state[i], v => { state[i] = typeof v === 'function' ? v(state[i]) : v }] },
    useRef(initial) { const i = ri++; return refs[i] ??= { current: initial } },
    useEffect(fn) { effects.push(fn) }, useCallback: fn => fn, useMemo: fn => fn(), useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
  }
  const jsx = (type, props) => ({ type, props })
  const source = ts.transpileModule(readFileSync(new URL(file, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText
  const exports = {}
  runInNewContext(source, { exports, require: name => ({ react, 'react/jsx-runtime': { jsx, jsxs: jsx, Fragment: 'fragment' }, ...modules })[name], queueMicrotask, AbortController, crypto: { randomUUID }, localStorage: { getItem: () => null, setItem() {} }, requestAnimationFrame: () => {}, document: { getElementById: () => null }, Map, Set, Intl })
  return { exports, render(fn) { si = 0; ri = 0; effects = []; return fn(exports) }, hydrate: () => effects[0](), effect: i => effects[i]() }
}
function nodes(tree) {
  if (Array.isArray(tree)) return tree.flatMap(nodes)
  if (!tree || typeof tree !== 'object') return []
  return [tree, ...nodes(tree.props?.children)]
}
function text(tree) {
  if (Array.isArray(tree)) return tree.map(text).join('')
  return tree && typeof tree === 'object' ? text(tree.props?.children) : tree == null || tree === false ? '' : String(tree)
}
const button = (tree, label) => nodes(tree).find(n => n.type === 'button' && text(n) === label)
const tick = () => new Promise(resolve => setImmediate(resolve))
const emptyQueue = () => ({ getSnapshot: () => ({ rows: [], storageError: false }), subscribe: () => () => {}, flush: async () => {}, discard: async () => {} })
test('real row status renders each result locally, preserves actionable retry/refresh/discard and success confirmation', () => {
  const h = component('../src/review/ReviewRowStatus.tsx'); let retries = 0; let refreshes = 0; let discards = 0
  for (const result of ['invalid', 'invalid_transition', 'stale_version', 'applied', 'duplicate']) {
    const row = { state: ['applied', 'duplicate'].includes(result) ? 'applied' : 'failed', result }
    const tree = h.exports.ReviewRowStatus({ row, retry: () => retries++, refresh: () => refreshes++, discard: () => discards++ })
    assert.equal(tree.props.role, 'status')
    if (row.state === 'failed') { button(tree, 'Reintentar envío').props.onClick(); button(tree, 'Actualizar palabra').props.onClick(); button(tree, 'Descartar decisión local').props.onClick() }
    else { assert.match(text(tree), /confirmada/); assert.equal(nodes(tree).filter(n => n.type === 'button').length, 0) }
  }
  assert.equal(retries, 3); assert.equal(refreshes, 3); assert.equal(discards, 3)
})
test('real approved pagination resets stale cursor400 to page1 and demote echoes freshly loaded version', async () => {
  const calls = []; const actions = []; const queue = { ...emptyQueue(), enqueue: async a => { actions.push(a) } }
  const h = component('../src/review/ApprovedPanel.tsx', {
    '../api/client': { ApiError }, '../api/review': { getApprovedWords: async (q, after) => { calls.push({ q, after }); if (after) throw new ApiError('http', 400); return { items: [approvedWord()], nextCursor: '1' } } }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' },
  })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  assert.match(text(render()), /Volvemos a la página 1/)
  h.hydrate(); await tick()
  assert.deepEqual(calls.map(c => c.after), [undefined, '1', undefined])
  await button(render(), 'Revisar otra vez').props.onClick(); await tick()
  assert.equal(actions[0].wordVersion, 7); assert.equal(actions[0].action, 'demote')
})
test('real alternatives handlers render variants read-only without headword change, offline lemma enqueues current version/ID', async () => {
  const word = reviewWord(); const saved = []; const queue = { ...emptyQueue(), enqueue: async (action, original) => { saved.push({ action, original }) } }
  const h = component('../src/ReviewQueue.tsx', {
    './review/cachedQueue': { getCachedQueue: () => ({ load: async () => ({ data: { items: [word], pendingTotal: 1, topics: [] }, offline: true, cacheFailed: false }) }) },
    './review/reviewModel': model, './review/ReviewEditor': { ReviewEditor: 'Editor' }, './review/ReviewRowStatus': { ReviewRowStatus: 'RowStatus' }, './review/ApprovedPanel': { ApprovedPanel: 'Approved' },
  })
  const render = () => h.render(e => { const pending = e.ReviewQueue({ username: 'reviewer', queue, approved: false, onComplete() {} }); return pending.type(pending.props) })
  render(); h.hydrate(); await tick()
  button(render(), 'No ✗').props.onClick({ currentTarget: null })
  button(render(), 'Variante regional (solo lectura)').props.onClick()
  let tree = render(); const variant = nodes(tree).find(n => n.props?.className === 'readonly-variant')
  assert.match(text(variant), /Solo lectura/); assert.equal(nodes(variant).filter(n => n.type === 'button').length, 0)
  assert.equal(word.headword, 'palabra-1'); assert.equal(saved.length, 0)
  button(tree, 'Variante de registro (solo lectura)').props.onClick(); tree = render()
  assert.match(text(nodes(tree).find(n => n.props?.className === 'readonly-variant')), /variante formal/)
  button(tree, 'Palabra').props.onClick(); tree = render()
  const alternative = nodes(tree).find(n => n.type === 'button' && text(n).includes('alternativa-1'))
  await alternative.props.onClick(); await tick()
  assert.equal(saved[0].action.action, 'approve_alt'); assert.equal(saved[0].action.wordVersion, 3); assert.equal(saved[0].action.alternativeId, 10)
  assert.equal(saved[0].original.headword, 'palabra-1')
})
for (const found of [true, false]) test(`stale demote automatically fetches approved page1; ${found ? 'fresh version enables new UUID' : 'missing word preserves actionable decision'}`, async () => {
  const calls = []; const actions = []; const discarded = []; let rows = []; let stale = false
  const oldAction = reviewAction(2, { action: 'demote', wordVersion: 7 })
  const queue = { ...emptyQueue(), getSnapshot: () => ({ rows, storageError: false }), enqueue: async a => { actions.push(a) }, discard: async id => { discarded.push(id); rows = rows.filter(r => r.action.clientReviewId !== id) } }
  const h = component('../src/review/ApprovedPanel.tsx', {
    '../api/client': { ApiError }, '../api/review': { getApprovedWords: async (q, after) => {
      calls.push({ q, after })
      return { items: after ? [approvedWord(2)] : stale && found ? [approvedWord(2, { version: 8 })] : [approvedWord(1)], nextCursor: 'page2' }
    } }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' },
  })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  assert.match(text(render()), /Página 2/)
  stale = true; rows = [{ action: oldAction, word: approvedWord(2), state: 'failed', result: 'stale_version' }]
  render(); const cleanup = h.effect(1); cleanup(); h.effect(1); await tick()
  const tree = render()
  assert.deepEqual(calls.map(c => c.after), [undefined, 'page2', undefined])
  assert.match(text(tree), /Página 1/)
  if (found) {
    assert.deepEqual(discarded, [oldAction.clientReviewId])
    assert.match(text(tree), /versión 8/)
    await button(tree, 'Revisar otra vez').props.onClick(); await tick()
    assert.equal(actions[0].wordVersion, 8)
    assert.notEqual(actions[0].clientReviewId, oldAction.clientReviewId)
  } else {
    assert.deepEqual(discarded, [])
    assert.equal(rows[0].result, 'stale_version')
    assert.match(text(tree), /Palabra no encontrada/)
    assert.ok(nodes(tree).some(n => n.type === 'RowStatus' && n.props.row === rows[0]))
    render(); h.effect(1); await tick()
    assert.equal(calls.length, 3, 'same stale result must not trigger a refresh loop')
  }
})
