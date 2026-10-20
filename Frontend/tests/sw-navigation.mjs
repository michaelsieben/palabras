import assert from 'node:assert/strict'
import { createECDH, createHash } from 'node:crypto'
import { readFile, writeFile } from 'node:fs/promises'
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { browserPlan } from './learnBrowser.mjs'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

// Test-only cookie gate + hostile URL-keyed proxy. No Playwright API mocks:
// worker-originated /me and every navigation reach real local HTTP middleware.
const workerFile = new URL('../dist/sw.js', import.meta.url)
const originalWorker = await readFile(workerFile, 'utf8')
const legacyWorker = `
const CACHE = 'palabras-shell-legacy-migration';
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(['/index.html', '/offline.htm'])).then(() => self.skipWaiting())));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.mode !== 'navigate') return;
  event.respondWith((async () => {
    if (['/', '/login', '/sesion'].includes(url.pathname)) return (await (await caches.open(CACHE)).match('/index.html')) || fetch(event.request);
    return fetch(event.request);
  })());
});
`
const digest = source => createHash('sha256').update(source).digest('hex')
const proxyEntries = new Map()
const requests = []
let force401 = false
let pushEnabled = true
const reminder = { enabled: true, time: '21:30', mutedUntil: '2026-10-23', windowMin: 90, activeDevices: 1 }
// Real P-256 public-key wire shape, test-only; no subscription or push service.
const fixtureKey = createECDH('prime256v1')
const pushConfig = { publicKey: fixtureKey.generateKeys().toString('base64url'), keyId: 'sw-navigation-fixture-key' }
const origin = testBaseUrl
const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true }, plugins: [{
  name: 'cookie-gate-hostile-proxy-fixture',
  configurePreviewServer(server) {
    server.middlewares.use(async (req, res, next) => {
      const url = new URL(req.url, origin)
      const signedIn = req.headers.cookie?.includes('fixture_session=owner') ?? false
      requests.push({ method: req.method, path: url.pathname + url.search, signedIn })
      if (url.pathname === '/api/auth/me') {
        res.statusCode = signedIn && !force401 ? 200 : 401
        res.setHeader('Content-Type', 'application/json')
        res.setHeader('Cache-Control', 'no-store')
        res.end(JSON.stringify(res.statusCode === 200 ? { username: 'fixture-owner', role: 'owner' } : { error: 'unauthorized' }))
      } else if (url.pathname === '/api/auth/login') {
        let body = ''
        for await (const chunk of req) body += chunk
        assert.equal(JSON.parse(body).password, 'fixture-only-password')
        assert.equal(req.headers['x-palabras'], '1')
        res.setHeader('Content-Type', 'application/json')
        res.setHeader('Set-Cookie', 'fixture_session=owner; Path=/; HttpOnly; SameSite=Lax')
        res.end(JSON.stringify({ username: 'fixture-owner', role: 'owner' }))
      } else if (url.pathname === '/api/push/config') {
        res.statusCode = pushEnabled ? 200 : 503
        res.setHeader('Content-Type', 'application/json')
        res.setHeader('Cache-Control', 'no-store')
        res.end(JSON.stringify(pushEnabled ? pushConfig : { error: 'push_disabled' }))
      } else if (url.pathname.startsWith('/api/learn/')) {
        // The authenticated learner view (App.tsx:64 → LearnSession) resolves its queue from
        // the API, so the reveal control only exists once a plan is served. Fixtures mirror
        // browser.mjs's fulfillLearn, but over real local HTTP like the auth endpoints above.
        res.setHeader('Content-Type', 'application/json')
        res.setHeader('Cache-Control', 'no-store')
        if (url.pathname === '/api/learn/reminder') res.end(JSON.stringify(reminder))
        else if (url.pathname.endsWith('/session/plan')) res.end(JSON.stringify(browserPlan()))
        else if (url.pathname.endsWith('/day/status')) res.end(JSON.stringify({ localDate: '2026-10-18', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null }))
        else if (url.pathname.endsWith('/words')) res.end(JSON.stringify({ items: [] }))
        else if (url.pathname.endsWith('/forecast')) res.end(JSON.stringify({ tomorrow: 2, next7Days: Array.from({ length: 7 }, (_, i) => ({ date: `2026-10-${19 + i}`, due: i === 0 ? 2 : i === 1 ? 7 : 0 })) }))
        else if (url.pathname.endsWith('/attempts')) {
          let body = ''
          for await (const chunk of req) body += chunk
          res.end(JSON.stringify({ results: JSON.parse(body).attempts.map(attempt => ({ clientAttemptId: attempt.clientAttemptId, status: 'applied' })) }))
        } else { res.statusCode = 404; res.end(JSON.stringify({ error: 'not_found' })) }
      } else if (url.pathname === '/legacy-bootstrap') {
        res.setHeader('Content-Type', 'text/html')
        res.end('<!doctype html><title>Legacy worker fixture</title>')
      } else if (url.pathname === '/sw.js') {
        // Deliberately ignore request Cache-Control and cache each script URL forever.
        // Plain /sw.js stays old even after replacing dist/sw.js on disk.
        let source = proxyEntries.get(req.url)
        if (!source) {
          source = url.searchParams.has('legacy') ? legacyWorker : url.search ? await readFile(workerFile, 'utf8') : originalWorker
          proxyEntries.set(req.url, source)
        }
        res.setHeader('Content-Type', 'text/javascript')
        res.setHeader('Cache-Control', 'public, max-age=86400')
        res.end(source)
      } else if (['/', '/sesion', '/revisar', '/aprobadas', '/index.html'].includes(url.pathname) && !signedIn) {
        res.statusCode = 302
        res.setHeader('Location', '/login?ReturnUrl=' + encodeURIComponent(url.pathname))
        res.end()
      } else next() // /proxy-stale-shell deliberately ignores cookies, like cached proxy HTML.
    })
  },
}] })

let browser
let phase = 'initial login and worker control'
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  const page = await context.newPage()
  const consoleLog = []; const failures = []; const pageErrors = []; const responses = []
  page.on('console', message => consoleLog.push({ type: message.type(), text: message.text() }))
  page.on('pageerror', error => pageErrors.push(error.message))
  page.on('requestfailed', request => failures.push({ url: request.url(), error: request.failure()?.errorText }))
  page.on('response', response => responses.push({ url: response.url(), status: response.status(), worker: response.fromServiceWorker() }))

  await page.goto(origin + '/')
  await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  await page.evaluate(async () => { await navigator.serviceWorker.ready; if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })) })
  const initialScript = await page.evaluate(() => navigator.serviceWorker.controller.scriptURL)
  assert.equal(new URL(initialScript).searchParams.get('version'), digest(originalWorker))
  assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistration()).updateViaCache), 'none')

  await page.getByLabel('Usuario', { exact: true }).fill('fixture-owner')
  await page.getByLabel('Contraseña', { exact: true }).fill('fixture-only-password')
  await page.getByLabel('Contraseña', { exact: true }).press('Enter')
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  // The reveal control only settles the learner plan, not the independent reminder
  // refresh or worker discovery. Finish those before toggling fixture config/reloading.
  await page.waitForLoadState('networkidle')
  // Check both configuration contracts over fixture HTTP, not page mocks. The expected
  // push-off 503 is probed outside page fetches so it cannot hide a console regression.
  const storedReminder = await page.request.get(origin + '/api/learn/reminder')
  assert.equal(storedReminder.status(), 200)
  assert.deepEqual(await storedReminder.json(), reminder)
  const enabledConfig = await page.request.get(origin + '/api/push/config')
  assert.equal(enabledConfig.status(), 200)
  assert.deepEqual(await enabledConfig.json(), pushConfig)
  pushEnabled = false
  const disabledConfig = await page.request.get(origin + '/api/push/config')
  assert.equal(disabledConfig.status(), 503)
  assert.deepEqual(await disabledConfig.json(), { error: 'push_disabled' })
  pushEnabled = true
  phase = 'controlled reload and reminder/config responses'
  consoleLog.length = 0; failures.length = 0; responses.length = 0
  const reloadRequestStart = requests.length
  const reload = await page.reload()
  assert.equal(reload.status(), 200)
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  await page.waitForLoadState('networkidle')
  // Assert persistent fixture/browser records after the finite load settles. No live
  // response subscription can consume an earlier refresh or leave an orphan waiter.
  for (const path of ['/api/learn/reminder', '/api/push/config']) {
    assert.ok(requests.slice(reloadRequestStart).some(request => request.path === path && request.signedIn), 'controlled reload requested ' + path)
    assert.ok(responses.some(response => new URL(response.url).pathname === path && response.status === 200 && !response.worker), 'controlled reload received network 200 for ' + path)
  }
  assert.equal(new URL(page.url()).pathname, '/')
  assert.deepEqual(failures, [])
  assert.deepEqual(pageErrors, [])
  assert.deepEqual(consoleLog.filter(message => message.type === 'error'), [])
  assert.ok(requests.slice(reloadRequestStart).some(request => request.path === '/api/auth/me' && request.signedIn))
  console.log('PROOF (a): real fixture POST sets HttpOnly cookie; immediate controlled reload returns 200, owner view remains; zero failed requests, console errors, page errors.')
  console.log('RELOAD NETWORK ' + JSON.stringify({ server: requests.slice(reloadRequestStart), browser: responses, console: consoleLog, failures, pageErrors }))

  await context.clearCookies()
  phase = 'anonymous navigation'
  const anonymousStart = requests.length
  await page.goto(origin + '/sesion')
  await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(new URL(page.url()).pathname, '/login')
  assert.equal(await page.getByRole('button', { name: 'Mostrar respuesta' }).count(), 0)
  const cachedPaths = await page.evaluate(async () => (await Promise.all((await caches.keys()).map(async key => (await (await caches.open(key)).keys()).map(request => new URL(request.url).pathname)))).flat())
  assert.ok(!cachedPaths.some(path => path === '/login' || path.endsWith('.html') || path.startsWith('/api')))
  assert.ok(requests.slice(anonymousStart).some(request => request.path === '/api/auth/me' && !request.signedIn))
  assert.ok(!requests.slice(anonymousStart).some(request => request.path === '/sesion'))
  assert.ok(requests.slice(anonymousStart).some(request => request.path.startsWith('/login')))
  console.log('PROOF (b): remove cookie, navigate /sesion; worker checks real /me 401, redirects to network /login; protected shell never fetched; CacheStorage has ' + cachedPaths.length + ' static paths and zero login/index/API entries.')

  await context.addCookies([{ name: 'fixture_session', value: 'owner', url: origin, httpOnly: true }])
  phase = '401 and offline navigation'
  force401 = true
  const raw401 = await page.evaluate(async () => { const response = await fetch('/api/auth/me', { cache: 'no-store' }); return { status: response.status, body: await response.text() } })
  assert.deepEqual(raw401, { status: 401, body: '{"error":"unauthorized"}' })
  const staleStart = requests.length
  await page.goto(origin + '/proxy-stale-shell')
  await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(new URL(page.url()).pathname, '/login')
  assert.ok(!requests.slice(staleStart).some(request => request.path === '/proxy-stale-shell'))
  assert.equal(await page.getByRole('heading', { name: 'Sin conexión' }).count(), 0)
  await context.setOffline(true)
  assert.equal(await page.evaluate(async () => { try { await fetch('/api/auth/me'); return 'masked' } catch { return 'rejected' } }), 'rejected')
  await page.goto(origin + '/sesion')
  await page.getByRole('heading', { name: 'Sin conexión' }).waitFor()
  await page.getByText('No se puede confirmar si has iniciado sesión. Conéctate para abrir Palabras.').waitFor()
  await context.setOffline(false)
  force401 = false
  console.log('PROOF (c): direct /me remains 401 with exact JSON; authority 401 prevents even deliberately public stale proxy shell; no offline fallback on 401; actual offline API rejects and offline navigation is session-neutral.')

  await page.goto(origin + '/')
  phase = 'two-tab worker replacement'
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  const second = await context.newPage()
  await second.goto(origin + '/')
  await second.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  await page.evaluate(async () => {
    const legacy = await caches.open('palabras-shell-legacy-fixture')
    await legacy.put('/assets/previous-Ab12cd.js', new Response('previous immutable chunk'))
    await legacy.put('/index.html', new Response('must not survive activation'))
    await legacy.put('/api/auth/me', new Response('must not survive activation'))
  })
  const changed = originalWorker.replace(/palabras-shell-[a-f0-9]+/, 'palabras-shell-automated-replacement')
  await writeFile(workerFile, changed) // Actual replacement of built /sw.js; restored in finally.
  const updateStart = requests.length
  const replacementURL = origin + '/sw.js?version=' + digest(changed)
  let pageLoads = 0; let secondLoads = 0
  page.on('load', () => pageLoads++)
  second.on('load', () => secondLoads++)
  await page.reload() // No reg.update(), unregister(), storage clearing, or manual cache clearing.
  await page.waitForFunction(url => navigator.serviceWorker.controller?.scriptURL === url, replacementURL)
  await second.waitForFunction(url => navigator.serviceWorker.controller?.scriptURL === url, replacementURL)
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  await second.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  assert.equal(pageLoads, 2, 'one requested load plus exactly one controller replacement reload')
  assert.equal(secondLoads, 1, 'other open tab reloads exactly once')
  const published = requests.slice(updateStart).filter(request => request.path.startsWith('/sw.js'))
  assert.ok(published.some(request => request.path === '/sw.js?version=' + digest(changed)))
  assert.equal(await (await page.request.get(origin + '/sw.js')).text(), originalWorker, 'plain proxy entry remains stale')
  assert.notEqual(initialScript, replacementURL)
  assert.equal(await page.evaluate(async () => (await fetch('/assets/previous-Ab12cd.js')).text()), 'previous immutable chunk')
  const remainingPaths = await page.evaluate(async () => (await Promise.all((await caches.keys()).map(async key => (await (await caches.open(key)).keys()).map(request => new URL(request.url).pathname)))).flat())
  assert.ok(!remainingPaths.some(path => path.endsWith('.html') || path.startsWith('/api')))
  console.log('PROOF (d): replaced dist/sw.js bytes; subsequent normal load discovers SHA-256 ' + digest(changed) + ', requests NEW registration URL despite 24h cached stale plain /sw.js; both tabs switch; requested tab has exactly 1 additional reload, second tab exactly 1 reload. No manual update/cache clearing.')
  console.log('UPDATE NETWORK ' + JSON.stringify(published))
  console.log('ATOMICITY: old hashed chunk remains fetchable after activation; legacy index.html and API cache entries deleted; new cache fully installed before skipWaiting; both tabs reload rather than continuing mixed documents.')

  // Discovery also runs on focus/visibility, without waiting for a navigation.
  // A replacement reload starts its own discovery. A synthetic focus during that
  // check is deliberately ignored by usePwa's `checking` guard; settle it first.
  await page.waitForLoadState('networkidle')
  const focused = changed + '\n/* focus-update */\n'
  phase = 'focus worker replacement'
  await writeFile(workerFile, focused)
  await page.evaluate(() => window.dispatchEvent(new Event('focus')))
  await page.waitForFunction(hash => navigator.serviceWorker.controller?.scriptURL.endsWith(hash), digest(focused))
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  await page.waitForLoadState('networkidle')
  const visible = focused + '\n/* visibility-update */\n'
  phase = 'visibility worker replacement'
  await writeFile(workerFile, visible)
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await page.waitForFunction(hash => navigator.serviceWorker.controller?.scriptURL.endsWith(hash), digest(visible))
  await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  console.log('ADDITIONAL: focus and visible visibilitychange independently discover new bytes, activate, reload. Public manifest/icons/sw paths and dev viewport covered by existing test:browser.')
  await context.close()
  await writeFile(workerFile, originalWorker)
  const legacyContext = await browser.newContext({ serviceWorkers: 'allow' })
  phase = 'legacy worker migration'
  const legacyPage = await legacyContext.newPage()
  await legacyPage.goto(origin + '/legacy-bootstrap')
  await legacyPage.evaluate(async () => {
    await navigator.serviceWorker.register('/sw.js?legacy=1', { scope: '/' })
    await navigator.serviceWorker.ready
    if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }))
  })
  const legacyURL = await legacyPage.evaluate(() => navigator.serviceWorker.controller.scriptURL)
  assert.ok(legacyURL.endsWith('?legacy=1'))
  // Unknown path reaches new document even with legacy cache-first known routes.
  await legacyPage.goto(origin + '/migration-bootstrap')
  await legacyPage.waitForFunction(hash => navigator.serviceWorker.controller?.scriptURL.endsWith(hash), digest(originalWorker))
  await legacyPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  await legacyPage.reload()
  await legacyPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(new URL(legacyPage.url()).pathname, '/login')
  assert.ok(!(await legacyPage.evaluate(() => caches.keys())).includes('palabras-shell-legacy-migration'))
  console.log('MIGRATION: installed legacy cache-first worker with protected index.html redirect in cache; network bootstrap to new client replaces it automatically; subsequent normal login reload succeeds, obsolete HTML cache removed. Does not prove recovery when old cached frontend never loads new client code.')
  await legacyContext.close()
} catch (error) {
  console.error('SW NAVIGATION FAILURE ' + JSON.stringify({ phase, requests }))
  throw error
} finally {
  await writeFile(workerFile, originalWorker)
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
