import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { ApiError } from '../src/api/client.ts'
import { getStats, isLocalDate, parseStats } from '../src/api/stats.ts'
import { answerTime, dayLabel, getStatsRange, percentage, rangeFrom, retentionLabel, statsError } from '../src/learn/statsFormat.ts'
import { statsCopy, statsText, stageCopy, modeCopy } from '../src/copy/stats.ts'

function fixture(from = '2026-10-18', to = from) {
  const days = []
  for (let ms = Date.parse(from); ms <= Date.parse(to); ms += 86400000)
    days.push({ date: new Date(ms).toISOString().slice(0, 10), answers: 0, hits: 0, misses: 0, newWords: 0, answerMs: 0 })
  return {
    localDate: '2026-10-18', from, to,
    totals: { answers: 0, hits: 0, misses: 0, formSlips: 0, overridden: 0, firstTry: 0, secondLook: 0, answerMs: 0, activeDays: 0, newWords: 0 },
    days, byMode: [], retention: { target: .9, all: { reviews: 0, passed: 0 }, esEn: { reviews: 0, passed: 0 }, enEs: { reviews: 0, passed: 0 } },
    words: { approved: 0, stages: { nuevo: 0, aprendiendo: 0, reciente: 0, consolidada: 0, dominada: 0 }, leechCards: 0, suspendedCards: { manual: 0, leech: 0 } },
    hardest: [], quality: { lateRows: 0, missingDurationRows: 0 },
  }
}
const invalid = body => assert.throws(() => parseStats(body), error => error instanceof ApiError && error.kind === 'invalid-response')
const set = (body, path, value, remove = false) => {
  const parts = path.split('.')
  const parent = parts.slice(0, -1).reduce((object, key) => object[key], body)
  if (remove) delete parent[parts.at(-1)]
  else parent[parts.at(-1)] = value
}

test('complete frozen camelCase contract accepts zero history, future modes and first-try groups != answers', () => {
  const body = fixture()
  assert.deepEqual(parseStats(body), body)
  body.totals = { ...body.totals, answers: 6, hits: 5, misses: 1, firstTry: 3, secondLook: 1 }
  body.byMode = [{ mode: 'future-mode', answers: 1, hits: 1, misses: 0, formSlips: 0 }]
  body.hardest = [{ key: 'cafe', article: null, headword: 'café', gloss: 'coffee', misses: 1 }]
  assert.deepEqual(parseStats(body), body)
})

test('every count is required, nonnegative safe integer, including nested/list fields', () => {
  const base = fixture()
  base.byMode = [{ mode: 'typed', answers: 0, hits: 0, misses: 0, formSlips: 0 }]
  base.hardest = [{ key: 'cafe', article: 'el', headword: 'café', gloss: 'coffee', misses: 0 }]
  const paths = [
    ...Object.keys(base.totals).map(key => `totals.${key}`),
    ...Object.keys(base.days[0]).filter(key => key !== 'date').map(key => `days.0.${key}`),
    ...Object.keys(base.byMode[0]).filter(key => key !== 'mode').map(key => `byMode.0.${key}`),
    ...['all', 'esEn', 'enEs'].flatMap(slice => ['reviews', 'passed'].map(key => `retention.${slice}.${key}`)),
    'words.approved', 'words.leechCards', ...Object.keys(base.words.stages).map(key => `words.stages.${key}`),
    'words.suspendedCards.manual', 'words.suspendedCards.leech', 'hardest.0.misses', 'quality.lateRows', 'quality.missingDurationRows',
  ]
  for (const path of paths) {
    for (const value of [-1, .5, '0', null, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
      const body = structuredClone(base); set(body, path, value); invalid(body)
    }
    const body = structuredClone(base); set(body, path, undefined, true); invalid(body)
  }
})

test('wrong containers, strings, target, impossible ratios and malformed lists reject', () => {
  for (const body of [null, [], {}, 'stats']) invalid(body)
  for (const [path, value] of [
    ['totals', []], ['days', {}], ['days.0', null], ['days.0.date', '2026-02-30'], ['byMode', null],
    ['byMode', [{ mode: 4, answers: 0, hits: 0, misses: 0, formSlips: 0 }]], ['retention', null],
    ['retention.target', -.01], ['retention.target', 1.01], ['retention.target', NaN], ['retention.target', '0.9'],
    ['retention.all.passed', 1], ['retention.esEn.passed', 1], ['retention.enEs.passed', 1],
    ['words.stages', []], ['words.suspendedCards', null], ['quality', []], ['totals.hits', 1], ['days.0.misses', 1],
    ['hardest', [null]], ['hardest', [{ key: 'x', headword: 'x', gloss: 'x', article: 2, misses: 1 }]],
    ['hardest', Array(6).fill({ key: 'x', headword: 'x', gloss: 'x', article: null, misses: 1 })],
  ]) { const body = fixture(); set(body, path, value); invalid(body) }
})

test('dense local-date range is ordered, inclusive, real, <=366 days and not future', () => {
  assert.equal(isLocalDate('2024-02-29'), true)
  for (const date of ['2026-02-29', '2026-2-01', '2026-13-01', '2026-10-18T00:00:00Z', null]) assert.equal(isLocalDate(date), false)
  assert.equal(parseStats(fixture('2026-10-12', '2026-10-18')).days.length, 7)
  for (const change of [
    body => body.days.pop(), body => body.days.reverse(), body => { body.days[1].date = body.days[0].date },
    body => { body.from = '2026-10-19' }, body => { body.to = '2026-10-19' }, body => { body.localDate = 'bad' },
  ]) { const body = fixture('2026-10-12', '2026-10-18'); change(body); invalid(body) }
  assert.equal(parseStats(fixture('2025-10-18', '2026-10-18')).days.length, 366)
  invalid(fixture('2025-10-17', '2026-10-18'))
})

test('client uses GET, cookie, no-store, query, signal, and no X-Palabras mutation header', async t => {
  let request
  const body = fixture('2026-10-12', '2026-10-18')
  t.mock.method(globalThis, 'fetch', async (path, init) => { request = { path, init }; return Response.json(body) })
  const signal = new AbortController().signal
  assert.deepEqual(await getStats({ from: body.from, to: body.to }, signal), body)
  assert.equal(request.path, '/api/learn/stats?from=2026-10-12&to=2026-10-18')
  assert.equal(request.init.method, 'GET')
  assert.equal(request.init.credentials, 'include')
  assert.equal(request.init.cache, 'no-store')
  assert.equal(request.init.headers.get('X-Palabras'), null)
  assert.equal(request.init.signal, signal)
  await getStats()
  assert.equal(request.path, '/api/learn/stats')
})

test('7/30/90 ranges use server local today and inclusive endpoints, not phone date', async t => {
  for (const [range, from] of [[7, '2026-10-12'], [30, '2026-09-19'], [90, '2026-07-21']]) {
    const paths = []
    t.mock.method(globalThis, 'fetch', async path => {
      paths.push(path)
      const query = new URLSearchParams(path.split('?')[1])
      return Response.json(fixture(query.get('from') ?? '2026-09-19', '2026-10-18'))
    })
    const data = await getStatsRange(range)
    assert.equal(data.from, from)
    assert.equal(data.days.length, range)
    assert.equal(paths[0], '/api/learn/stats')
    assert.equal(paths.length, range === 30 ? 1 : 2)
    if (range !== 30) assert.equal(paths[1], `/api/learn/stats?from=${from}&to=2026-10-18`)
  }
  assert.equal(rangeFrom('2027-03-29', 7), '2027-03-23') // spring DST
  assert.equal(rangeFrom('2026-10-27', 7), '2026-10-21') // autumn DST
  assert.equal(rangeFrom('2024-03-01', 7), '2024-02-24') // leap day
  assert.throws(() => rangeFrom('bad', 7), RangeError)
})

test('percentages round in Spanish, zero sample is dash; retention cutoff applies at 20 per slice', () => {
  assert.equal(percentage(23, 25), '92\u00a0%')
  assert.equal(percentage(1, 3), '33\u00a0%')
  assert.equal(percentage(0, 1), '0\u00a0%')
  assert.equal(percentage(1, 1), '100\u00a0%')
  assert.equal(percentage(0, 0), '—')
  assert.equal(retentionLabel({ reviews: 0, passed: 0 }), 'Pocos datos todavía')
  assert.equal(retentionLabel({ reviews: 19, passed: 19 }), 'Pocos datos todavía')
  assert.equal(retentionLabel({ reviews: 20, passed: 18 }), '90\u00a0%')
})

test('date labels never shift with host timezone, and time is floored not inflated', () => {
  const previous = process.env.TZ
  try {
    for (const zone of ['Pacific/Honolulu', 'Pacific/Kiritimati', 'Europe/Madrid']) {
      process.env.TZ = zone
      assert.equal(dayLabel('2026-10-18'), '18 oct')
      assert.equal(dayLabel('2024-02-29'), '29 feb')
    }
  } finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous }
  assert.throws(() => dayLabel('2026-02-30'), RangeError)
  for (const [ms, text] of [[0, '0 s'], [999, '0 s'], [59999, '59 s'], [60000, '1 min 0 s'], [3599999, '59 min 59 s'], [3600000, '1 h 0 min'], [7261000, '2 h 1 min']]) assert.equal(answerTime(ms), text)
})

test('401, 403, invalid response, and network errors retain sensible distinct Spanish actions', async t => {
  for (const [status, kind] of [[401, 'unauthorized'], [403, 'forbidden'], [500, 'error']]) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ error: 'failure' }, { status }))
    await assert.rejects(getStats(), error => statsError(error) === kind)
    assert.ok(statsCopy[kind].length)
  }
  t.mock.method(globalThis, 'fetch', async () => { throw new TypeError('Network down') })
  await assert.rejects(getStats(), error => error.kind === 'network' && statsError(error) === 'error')
  t.mock.method(globalThis, 'fetch', async () => Response.json({}))
  await assert.rejects(getStats(), error => statsError(error) === 'error')
  assert.equal(statsError(new Error('unknown')), 'error')
})

test('keyed copy names estimates, range-scoped first tries, offline, pending, and exact stage definitions', () => {
  assert.equal(statsCopy.offline, 'Progreso disponible al conectar.')
  assert.equal(statsText('pending', { n: 3 }), '3 respuestas pendientes de envío. Cifras anteriores a su sincronización.')
  assert.match(statsCopy.noAnswers, /Sin respuestas en este periodo/u)
  assert.match(statsCopy.noWords, /Aún no hay palabras aprobadas/u)
  assert.match(statsCopy.firstScope, /dentro del periodo/u)
  assert.match(statsCopy.firstScope, /si una sesión empieza antes/u)
  assert.match(statsCopy.retentionEstimate, /Estimación/u)
  assert.match(statsCopy.dayEstimate, /aproximado/u)
  assert.match(statsCopy.stability, /estimación/u)
  assert.match(statsText('estimated', { time: '2 min' }), /^Estimación mínima · 2 min$/u)
  assert.match(statsCopy.timeCaveat, /no incluye leer la explicación ni elegir la calificación/u)
  assert.match(statsCopy.appTime, /Tiempo total en la app: llega.*M7/u)
  assert.match(statsCopy.late, /aviso, no un recuento de fechas erróneas/u)
  assert.deepEqual(stageCopy.reciente, ['Reciente', 'En repaso'])
  assert.deepEqual(stageCopy.consolidada, ['Consolidada', 'Estabilidad ≥ 21 días'])
  assert.deepEqual(stageCopy.dominada, ['Dominada', 'Estabilidad ≥ 90 días'])
  assert.equal(modeCopy.cloze, 'Completa la frase')
})

test('charts expose text alternatives and daily table; hardest links feed existing word search', () => {
  const page = readFileSync(new URL('../src/learn/StatsPage.tsx', import.meta.url), 'utf8')
  assert.equal((page.match(/role="img" aria-label=/gu) ?? []).length, 2)
  assert.match(page, /<th scope="row"><time dateTime=\{day.date\}/u)
  assert.match(page, /<th scope="col">\{statsCopy.hits\}/u)
  assert.match(page, /<th scope="col">\{statsCopy.misses\}/u)
  assert.match(page, /\/palabras\?q=\$\{encodeURIComponent\(word.headword\)\}/u)
  const words = readFileSync(new URL('../src/learn/WordList.tsx', import.meta.url), 'utf8')
  assert.match(words, /new URLSearchParams\(window.location.search\).get\('q'\)/u)
  const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8')
  assert.match(app, /path === '\/progreso' \? \(attemptQueue \? <StatsPage/u)
  assert.match(app, /\['\/progreso', statsCopy.progress\]/u)
})
