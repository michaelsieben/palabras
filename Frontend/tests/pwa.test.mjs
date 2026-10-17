import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile, mkdtemp, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createContext, runInContext } from 'node:vm'
import { collectFiles, generateWorker, workerSource } from '../scripts/pwa.mjs'

test('manifest provides standalone Spanish app and real correctly sized maskable/any PNGs', async () => {
  const manifest = JSON.parse(await readFile(new URL('../public/manifest.webmanifest', import.meta.url)))
  assert.equal(manifest.display, 'standalone'); assert.equal(manifest.lang, 'es-ES')
  assert.equal(manifest.scope, '/'); assert.equal(manifest.orientation, 'any')
  assert.equal(manifest.start_url, '/'); assert.equal(manifest.id, '/')
  for (const icon of manifest.icons) {
    const png = await readFile(new URL(`../public${icon.src}`, import.meta.url))
    assert.deepEqual([...png.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10])
    assert.equal(`${png.readUInt32BE(16)}x${png.readUInt32BE(20)}`, icon.sizes)
  }
  for (const purpose of ['any', 'maskable']) for (const size of ['192x192', '512x512']) assert.ok(manifest.icons.some(icon => icon.purpose === purpose && icon.sizes === size))
  for (const screenshot of manifest.screenshots) {
    const png = await readFile(new URL(`../public${screenshot.src}`, import.meta.url))
    assert.equal(`${png.readUInt32BE(16)}x${png.readUInt32BE(20)}`, screenshot.sizes)
  }
})

test('cache version is deterministic and changes when public/app content changes', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'palabras-pwa-'))
  try {
    await writeFile(join(directory, 'index.html'), 'build one')
    await generateWorker(directory)
    const one = await readFile(join(directory, 'sw.js'), 'utf8')
    await generateWorker(directory)
    assert.equal(await readFile(join(directory, 'sw.js'), 'utf8'), one)
    await writeFile(join(directory, 'index.html'), 'build two')
    await generateWorker(directory)
    assert.notEqual(await readFile(join(directory, 'sw.js'), 'utf8'), one)
    assert.ok(!one.includes('"/sw.js"'))
  } finally { await rm(directory, { recursive: true }) }
})

function harness(offline = false) {
  const events = {}; const deleted = []; let skipped = false; let claimed = false
  const context = createContext({
    URL,
    self: { location: { origin: 'https://palabras.test' }, addEventListener: (event, handler) => { events[event] = handler }, skipWaiting: () => { skipped = true }, clients: { claim: async () => { claimed = true } } },
    caches: { open: async () => ({ addAll: async urls => { assert.ok(urls.includes('/index.html')) }, match: async url => `cached:${url}` }), keys: async () => ['palabras-shell-old', 'palabras-shell-new', 'other-app'], delete: async key => { deleted.push(key) } },
    fetch: async () => { if (offline) throw new Error('offline'); return 'network' },
  })
  runInContext(workerSource('new', ['/index.html', '/offline.html', '/assets/app.js']), context)
  return { events, deleted, skipped: () => skipped, claimed: () => claimed }
}

test('update waits for explicit message, then only deletes this app old caches and claims clients', async () => {
  const h = harness()
  let pending
  h.events.install({ waitUntil: promise => { pending = promise } }); await pending
  assert.equal(h.skipped(), false)
  h.events.message({ data: { type: 'OTHER' } }); assert.equal(h.skipped(), false)
  h.events.message({ data: { type: 'SKIP_WAITING' } }); assert.equal(h.skipped(), true)
  h.events.activate({ waitUntil: promise => { pending = promise } }); await pending
  assert.deepEqual(h.deleted, ['palabras-shell-old']); assert.equal(h.claimed(), true)
})

test('offline navigation serves coherent shell for known routes and real fallback for unknown route', async () => {
  const h = harness(true)
  for (const route of ['/', '/sesion', '/palabras', '/revisar', '/ajustes', '/aprobadas', '/not-cached']) {
    let response
    h.events.fetch({ request: { url: `https://palabras.test${route}`, method: 'GET', mode: 'navigate' }, respondWith: promise => { response = promise } })
    assert.equal(await response, route === '/not-cached' ? 'cached:/offline.html' : 'cached:/index.html')
  }
})

test('SW never caches backend/private requests, manifest, worker, external origins, or mutations', () => {
  const h = harness()
  for (const [url, method] of [['/api/learn/plan', 'GET'], ['/manifest.webmanifest', 'GET'], ['/sw.js', 'GET'], ['/sesion', 'POST'], ['https://external.test/a', 'GET']]) {
    h.events.fetch({ request: { url: url.startsWith('https') ? url : `https://palabras.test${url}`, method, mode: 'cors' }, respondWith: () => { assert.fail('must use network without interception') } })
  }
})

test('built output includes manifest/icons/worker/fallback but excludes dev diagnostic chunk', async () => {
  const files = await collectFiles(fileURLToPath(new URL('../dist/', import.meta.url)))
  for (const name of ['manifest.webmanifest', 'sw.js', 'offline.html', 'icon-192.png', 'icon-512.png', 'maskable-192.png', 'maskable-512.png']) assert.ok(files.some(file => file.endsWith(name)), `${name} emitted`)
  for (const file of files.filter(file => file.endsWith('.js'))) {
    const source = await readFile(file, 'utf8')
    assert.ok(!source.includes('Diagnóstico en vivo'), 'debug implementation excluded from production')
  }
  console.log(`Built-output verification: ${files.length} files, all PWA assets present; debug implementation absent.`)
})
