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
function component(file, modules = {}, transform = source => source) {
  const state = []; const refs = []; let si = 0; let ri = 0; let effects = []
  const react = {
    useState(initial) { const i = si++; if (!(i in state)) state[i] = typeof initial === 'function' ? initial() : initial; return [state[i], v => { state[i] = typeof v === 'function' ? v(state[i]) : v }] },
    useRef(initial) { const i = ri++; return refs[i] ??= { current: initial } },
    useEffect(fn) { effects.push(fn) }, useCallback: fn => fn, useMemo: fn => fn(), useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
  }
  const jsx = (type, props) => ({ type, props })
  const source = ts.transpileModule(transform(readFileSync(new URL(file, import.meta.url), 'utf8')), { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText
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
test('pending decisions disable word refresh and stale refresh handlers cannot discard unsent outbox entries', async () => {
  let row = { action: reviewAction(7), word: approvedWord(7), state: 'failed', result: 'invalid' }
  let calls = 0; const discarded = []
  const queue = { ...emptyQueue(), getSnapshot: () => ({ rows: [row], storageError: false }), discard: async id => discarded.push(id) }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/review': { getApprovedWords: async () => { calls++; return { items: [approvedWord(7)] } } }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  const staleRefresh = nodes(render()).find(n => n.type === 'RowStatus').props.refresh
  row = { ...row, state: 'pending', result: undefined }
  await staleRefresh(); await tick()
  assert.equal(calls, 1); assert.deepEqual(discarded, [])
  const status = component('../src/review/ReviewRowStatus.tsx').exports.ReviewRowStatus({ ...nodes(render()).find(n => n.type === 'RowStatus').props })
  assert.equal(button(status, 'Actualización bloqueada: decisión pendiente').props.disabled, true)
  assert.equal(button(status, 'Descartar decisión local'), undefined)
  assert.equal(row.action.clientReviewId, reviewAction(7).clientReviewId)
})
test('failed new search cannot expose the previous query next cursor after loading ends', async () => {
  const calls = []
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': { getApprovedWords: async (q, after) => {
    calls.push({ q, after })
    if (q) throw new ApiError('network')
    return { items: [approvedWord(7)], nextCursor: 'old-query-cursor' }
  } } })
  const render = () => h.render(e => e.ApprovedPanel({ queue: emptyQueue() }))
  render(); h.hydrate(); await tick()
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  assert.match(text(render()), /Página 2/)
  nodes(render()).find(n => n.type === 'input').props.onChange({ target: { value: 'nuevo' } })
  nodes(render()).find(n => n.type === 'form').props.onSubmit({ preventDefault() {} })
  render(); h.hydrate(); await tick()
  assert.deepEqual(calls.at(-1), { q: 'nuevo', after: undefined })
  assert.match(text(render()), /Página 1/)
  assert.match(text(render()), /No se han podido cargar/)
  assert.equal(button(render(), 'Siguiente').props.disabled, true, 'failed search must not reuse an old next cursor')
  assert.equal(button(render(), 'Anterior').props.disabled, true)
})
test('new and repeated search walks reset page and next before a deferred outbox flush, invalidating older reads', async () => {
  const calls = []; let releaseFlush; let releaseRead
  const queue = { ...emptyQueue(), flush: async () => { if (releaseFlush === null) await new Promise(resolve => { releaseFlush = resolve }) } }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/review': { getApprovedWords: async (q, after) => {
    calls.push({ q, after })
    if (after) return new Promise(resolve => { releaseRead = resolve })
    return { items: [approvedWord(7)], nextCursor: '7' }
  } } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  nodes(render()).find(n => n.type === 'input').props.onChange({ target: { value: ' café ' } })
  nodes(render()).find(n => n.type === 'form').props.onSubmit({ preventDefault() {} })
  assert.equal(button(render(), 'Siguiente').props.disabled, true)
  assert.equal(button(render(), 'Anterior').props.disabled, true)
  releaseRead({ items: [], nextCursor: 'old-cursor' }); await tick()
  assert.equal(button(render(), 'Siguiente').props.disabled, true)
  releaseFlush = null; render(); h.hydrate(); await tick()
  assert.equal(calls.length, 2)
  assert.equal(button(render(), 'Siguiente').props.disabled, true)
  releaseFlush(); await tick()
  assert.deepEqual(calls.at(-1), { q: 'café', after: undefined })
  assert.match(text(render()), /Página 1/)
  nodes(render()).find(n => n.type === 'form').props.onSubmit({ preventDefault() {} })
  assert.equal(button(render(), 'Siguiente').props.disabled, true)
  render(); h.hydrate(); await tick()
  assert.deepEqual(calls.slice(-2), [{ q: 'café', after: undefined }, { q: 'café', after: undefined }])
  assert.match(text(render()), /Página 1/)
})
for (const outcome of ['approved', 'rejected', 'pending_review', 'missing', 'network']) test(`invalid_transition refresh is on demand, nonretryable, and ${outcome} recovery is authoritative`, async () => {
  const row = { action: reviewAction(7, { action: 'reject' }), word: approvedWord(7), state: 'failed', result: 'invalid_transition' }
  let rows = [row]; const calls = []; const discarded = []
  const queue = { ...emptyQueue(), getSnapshot: () => ({ rows, storageError: false }), discard: async id => { discarded.push(id); rows = [] } }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/review': {
    getApprovedWords: async () => ({ items: [] }),
    getReviewQueue: async (_signal, scope, after) => {
      calls.push({ scope, after })
      if (outcome === 'network') throw new ApiError('network')
      return after ? { items: outcome === 'missing' ? [] : [reviewWord(7, { status: outcome, version: 12 })] } : { items: [reviewWord(1, { status: 'rejected' })], nextAfter: 'd:1' }
    },
  }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick(); render(); h.effect(1); await tick()
  assert.deepEqual(calls, [], 'conflict must not auto-fetch')
  const props = nodes(render()).find(n => n.type === 'RowStatus').props
  const status = component('../src/review/ReviewRowStatus.tsx').exports.ReviewRowStatus(props)
  assert.equal(button(status, 'Reintentar envío'), undefined)
  button(status, 'Actualizar palabra').props.onClick(); await tick()
  assert.deepEqual(calls, outcome === 'network' ? [{ scope: 'all', after: undefined }] : [{ scope: 'all', after: undefined }, { scope: 'all', after: 'd:1' }])
  if (['missing', 'network'].includes(outcome)) {
    assert.deepEqual(discarded, []); assert.equal(rows[0], row)
    assert.ok(nodes(render()).some(n => n.type === 'RowStatus'))
  } else {
    assert.deepEqual(discarded, [row.action.clientReviewId])
    const tree = render(); assert.match(text(tree), /versión 12/)
    assert.match(text(tree), outcome === 'approved' ? /Aprobada/ : outcome === 'rejected' ? /Retirada/ : /Pendiente/)
    assert.equal(nodes(tree).some(n => n.type === 'RowStatus'), false)
    assert.equal(!!button(tree, 'Retirar'), outcome === 'approved')
    assert.equal(!!button(tree, 'Volver a incluir'), outcome === 'rejected')
  }
  render(); h.effect(1); await tick()
  assert.equal(calls.length, outcome === 'network' ? 1 : 2, 'no conflict refresh loop')
})
async function cursorRestartScenario(scope, transform) {
  const calls = []; const actions = []; let changed = false
  const queue = { ...emptyQueue(), enqueue: async a => { actions.push(a) }, flush: async () => { if (actions.length) changed = true } }
  const read = async (q, after) => {
    calls.push({ q, after, scope })
    if (after && changed) throw new ApiError('http', 400, { error: 'bad_request' })
    // After mutation the old row leaves the scope; deliberately re-advertise its cursor to test the loop guard.
    return { items: [scope === 'approved' ? approvedWord(changed ? 8 : 7) : reviewWord(changed ? 8 : 7, { status: 'rejected' })], nextCursor: scope === 'approved' ? '7' : 'd:7' }
  }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': {
    getApprovedWords: read,
    getReviewQueue: async (_signal, status, after) => { assert.equal(status, 'rejected'); const page = await read('', after); return { items: page.items, nextAfter: page.nextCursor } },
  }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } }, transform)
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  if (scope === 'approved') {
    nodes(render()).find(n => n.type === 'input').props.onChange({ target: { value: 'café' } })
    nodes(render()).find(n => n.type === 'form').props.onSubmit({ preventDefault() {} })
  } else button(render(), 'Retiradas').props.onClick()
  render(); h.hydrate(); await tick(); calls.length = 0
  await button(render(), scope === 'approved' ? 'Retirar' : 'Volver a incluir').props.onClick(); await tick()
  button(render(), 'Siguiente').props.onClick(); render()
  // Concurrent refreshes share the generation guard; only latest failure can reset the walk.
  h.hydrate(); h.hydrate(); await tick()
  if (transform) return { calls, previousDisabled: button(render(), 'Anterior').props.disabled, failed: text(render()).includes('No se han podido cargar') }
  assert.equal(button(render(), 'Anterior').props.disabled, true, 'recovery must reset the walk to page1')
  assert.match(text(render()), /Volvemos a la página 1/)
  h.hydrate(); await tick()
  assert.equal(calls.filter(c => c.after === undefined).length, 1)
  assert.match(text(render()), /Página 1/)
  assert.ok(calls.every(c => c.scope === scope && c.q === (scope === 'approved' ? 'café' : '')))
  assert.equal(actions[0].wordId, 7, 'mutation removed the cursor row from its scope')
  const before = calls.length
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  assert.match(text(render()), /No se han podido cargar/)
  assert.match(text(render()), /Página 2/)
  assert.equal(calls.length, before + 1, 'same cursor must not restart twice')
  return calls
}
for (const scope of ['approved', 'rejected']) test(`${scope} cursor400 after mutation coalesces page1 restart, preserves filters, and never repeats same cursor restart`, async () => {
  const calls = await cursorRestartScenario(scope)
  assert.ok(calls.some(c => c.after === (scope === 'approved' ? '7' : 'd:7')))
})
test('disabled cursor400 recovery leaves page2 error with no page1 request in both scopes', async () => {
  for (const scope of ['approved', 'rejected']) {
    const observed = await cursorRestartScenario(scope, source => {
    const branch = 'failure instanceof ApiError && failure.status === 400 && after'
    assert.ok(source.includes(branch))
    return source.replace(branch, 'false')
    })
    assert.equal(observed.previousDisabled, false)
    assert.equal(observed.failed, true)
    assert.equal(observed.calls.filter(c => c.after === undefined).length, 0)
    assert.ok(observed.calls.every(c => c.after === (scope === 'approved' ? '7' : 'd:7')))
  }
})
test('real row status renders each result locally, preserves actionable retry/refresh/discard and success confirmation', () => {
  const h = component('../src/review/ReviewRowStatus.tsx'); let retries = 0; let refreshes = 0; let discards = 0
  for (const result of ['invalid', 'invalid_transition', 'stale_version', 'applied', 'duplicate']) {
    const row = { state: ['applied', 'duplicate'].includes(result) ? 'applied' : 'failed', result }
    const tree = h.exports.ReviewRowStatus({ row, retry: () => retries++, refresh: () => refreshes++, discard: () => discards++ })
    assert.equal(tree.props.role, 'status')
    if (row.state === 'failed') { if (result === 'invalid_transition') assert.equal(button(tree, 'Reintentar envío'), undefined); else button(tree, 'Reintentar envío').props.onClick(); button(tree, 'Actualizar palabra').props.onClick(); button(tree, 'Descartar decisión local').props.onClick() }
    else { assert.match(text(tree), /confirmada/); assert.equal(nodes(tree).filter(n => n.type === 'button').length, 0) }
  }
  assert.equal(retries, 2); assert.equal(refreshes, 3); assert.equal(discards, 3)
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
for (const action of ['demote', 'reject']) for (const found of [true, false]) test(`stale ${action} automatically fetches approved page1; ${found ? 'fresh version enables new UUID' : 'missing word preserves actionable decision'}`, async () => {
  const calls = []; const actions = []; const discarded = []; let rows = []; let stale = false
  const oldAction = reviewAction(2, { action, wordVersion: 7 })
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
    await button(tree, action === 'reject' ? 'Retirar' : 'Revisar otra vez').props.onClick(); await tick()
    assert.equal(actions[0].wordVersion, 8)
    assert.equal(actions[0].action, action)
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
test('approved objection enqueues reject with displayed version and consequence; repeated active filter does not clear list', async () => {
  const saved = []; const queue = { ...emptyQueue(), enqueue: async (action, word) => saved.push({ action, word }) }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': { getApprovedWords: async () => ({ items: [approvedWord(5, { version: 11 })] }) }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  assert.match(text(render()), /Se pausarán sus tarjetas; el historial se conservará/)
  button(render(), 'Aprobadas').props.onClick()
  assert.match(text(render()), /versión 11/)
  await button(render(), 'Retirar').props.onClick(); await tick()
  assert.equal(saved[0].action.action, 'reject'); assert.equal(saved[0].action.wordVersion, 11)
  assert.equal(saved[0].word.status, 'approved'); assert.equal(saved[0].word.wordId, 5)
})
test('rejected filter pages with explicit status and reversal uses authoritative row version', async () => {
  const calls = []; const saved = []
  const queue = { ...emptyQueue(), enqueue: async (action, word) => saved.push({ action, word }) }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': {
    getApprovedWords: async () => ({ items: [] }),
    getReviewQueue: async (signal, status, after) => { calls.push({ status, after }); return { items: [reviewWord(after ? 8 : 7, { status: 'rejected', version: 15 })], nextAfter: after ? null : 'd:7' } },
  }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  button(render(), 'Retiradas').props.onClick(); render(); h.hydrate(); await tick()
  assert.match(text(render()), /Retirada/); assert.match(text(render()), /volverán como nuevas/)
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  assert.deepEqual(calls, [{ status: 'rejected', after: undefined }, { status: 'rejected', after: 'd:7' }])
  await button(render(), 'Volver a incluir').props.onClick(); await tick()
  assert.equal(saved[0].action.action, 'approve'); assert.equal(saved[0].action.wordId, 8); assert.equal(saved[0].action.wordVersion, 15)
  assert.equal(saved[0].word.status, 'rejected')
})
for (const found of [true, false]) test(`rejected stale reversal refreshes page1; ${found ? 'fresh version actionable' : 'missing row stays durable'}`, async () => {
  const calls = []; const discarded = []; const saved = []; let rows = []; let stale = false
  const old = reviewAction(8, { action: 'approve', wordVersion: 7 })
  const queue = { ...emptyQueue(), getSnapshot: () => ({ rows, storageError: false }), discard: async id => { discarded.push(id); rows = rows.filter(r => r.action.clientReviewId !== id) }, enqueue: async a => saved.push(a) }
  const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': {
    getApprovedWords: async () => ({ items: [] }),
    getReviewQueue: async (_signal, status, after) => { calls.push({ status, after }); return { items: [reviewWord(stale && found ? 8 : 7, { status: 'rejected', version: stale ? 9 : 7 })], nextAfter: 'd:7' } },
  }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
  const render = () => h.render(e => e.ApprovedPanel({ queue }))
  render(); h.hydrate(); await tick()
  button(render(), 'Retiradas').props.onClick(); render(); h.hydrate(); await tick()
  button(render(), 'Siguiente').props.onClick(); render(); h.hydrate(); await tick()
  stale = true; rows = [{ action: old, word: reviewWord(8, { status: 'rejected', version: 7 }), state: 'failed', result: 'stale_version' }]
  render(); const cleanup = h.effect(1); cleanup(); h.effect(1); await tick()
  assert.deepEqual(calls.map(c => c.after), [undefined, 'd:7', undefined]); assert.ok(calls.every(c => c.status === 'rejected'))
  assert.match(text(render()), /Página 1/)
  if (found) {
    assert.match(text(render()), /La palabra ha cambiado/); assert.deepEqual(discarded, [old.clientReviewId])
    const card = nodes(render()).find(n => n.type === 'article' && text(n).includes('palabra-8'))
    await button(card, 'Volver a incluir').props.onClick(); await tick()
    assert.equal(saved[0].wordVersion, 9); assert.notEqual(saved[0].clientReviewId, old.clientReviewId)
  } else {
    assert.deepEqual(discarded, []); assert.ok(nodes(render()).some(n => n.type === 'RowStatus' && n.props.row === rows[0]))
    render(); h.effect(1); await tick(); assert.equal(calls.length, 3)
  }
})
test('objection invalid_transition and duplicate stay on row across filters; confirmed refresh wired', async () => {
  for (const result of ['invalid_transition', 'duplicate']) {
    const row = { action: reviewAction(3, { action: 'reject', wordVersion: 7 }), word: approvedWord(3, { status: 'approved' }), state: result === 'duplicate' ? 'applied' : 'failed', result }
    let rows = [row]
    // First hydration failing must not discard acknowledged rows without a successful server read.
    const queue = { ...emptyQueue(), getSnapshot: () => ({ rows, storageError: false }), discard: async () => { rows = [] } }
    const h = component('../src/review/ApprovedPanel.tsx', { '../api/client': { ApiError }, '../api/review': { getApprovedWords: async () => { throw new ApiError('network') }, getReviewQueue: async () => { throw new ApiError('network') } }, './ReviewRowStatus': { ReviewRowStatus: 'RowStatus' } })
    const render = () => h.render(e => e.ApprovedPanel({ queue }))
    render(); h.hydrate(); await tick()
    button(render(), 'Retiradas').props.onClick(); render(); h.hydrate(); await tick()
    const status = nodes(render()).find(n => n.type === 'RowStatus')
    assert.equal(status.props.row.result, result); assert.equal(status.props.confirmedRefresh, true)
    assert.match(text(render()), /Aprobada/); assert.match(text(render()), /estado leído antes/)
  }
})
test('confirmed duplicate row offers explicit server refresh without another transport or retry', () => {
  const h = component('../src/review/ReviewRowStatus.tsx'); let refreshes = 0
  const tree = h.exports.ReviewRowStatus({ row: { state: 'applied', result: 'duplicate' }, confirmedRefresh: true, refresh: () => refreshes++, retry() {}, discard() {} })
  assert.match(text(tree), /Decisión ya confirmada/); button(tree, 'Actualizar palabra').props.onClick()
  assert.equal(refreshes, 1); assert.equal(nodes(tree).filter(n => n.type === 'button').length, 1)
})
