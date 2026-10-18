import test from 'node:test'
import assert from 'node:assert/strict'
import { loadCachedPlan } from '../src/learn/cachedPlan.ts'
import { getForecast } from '../src/api/learn.ts'

const plan = { sessionId: 'session', generatedAtUtc: '2026-10-18T12:00:00Z', localDate: '2026-10-18', items: [],
  counts: { due: 0, newOffered: 0, backlog: 0 }, warnings: [] }
test('cache scoped by account; network/server failure fallback; corrupt cache and 401/403 cannot open session', async t => {
  const rows = new Map()
  const previous = globalThis.localStorage
  globalThis.localStorage = { getItem: k => rows.get(k), setItem: (k, v) => rows.set(k, v) }
  t.after(() => { if (previous === undefined) delete globalThis.localStorage; else globalThis.localStorage = previous })
  t.mock.method(globalThis, 'fetch', async () => Response.json(plan))
  assert.deepEqual(await loadCachedPlan('owner'), { plan, offline: false })
  globalThis.fetch = async () => { throw new Error('offline') }
  assert.deepEqual(await loadCachedPlan('owner'), { plan, offline: true })
  await assert.rejects(loadCachedPlan('other'), /network/)
  for (const status of [401, 403]) {
    globalThis.fetch = async () => Response.json({ error: 'denied' }, { status })
    await assert.rejects(loadCachedPlan('owner'))
  }
  globalThis.fetch = async () => Response.json({ error: 'unavailable' }, { status: 500 })
  assert.deepEqual(await loadCachedPlan('owner'), { plan, offline: true })
  globalThis.fetch = async () => { throw new Error('offline') }
  rows.set('palabras-plan:owner', '{bad')
  await assert.rejects(loadCachedPlan('owner'), /network/)
})

test('forecast validates counts and uses frozen route', async t => {
  const forecast = { tomorrow: 2, next7Days: Array.from({ length: 7 }, (_, i) => ({ date: `2026-10-${19 + i}`, due: i === 0 ? 2 : i === 1 ? 7 : 0 })) }
  t.mock.method(globalThis, 'fetch', async path => { assert.equal(path, '/api/learn/forecast'); return Response.json(forecast) })
  assert.deepEqual(await getForecast(), forecast)
  assert.equal(forecast.next7Days.reduce((sum, day) => sum + day.due, 0), 9)
  for (const bad of [{ ...forecast, tomorrow: -1 }, { tomorrow: 2, next7Days: 9 },
    { ...forecast, next7Days: forecast.next7Days.slice(0, 6) },
    { ...forecast, next7Days: forecast.next7Days.map(day => ({ ...day, due: -1 })) }]) {
    globalThis.fetch = async () => Response.json(bad)
    await assert.rejects(getForecast(), /invalid-response/)
  }
})
