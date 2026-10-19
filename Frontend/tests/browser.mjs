import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { createServer, preview } from 'vite'
import { readFile } from 'node:fs/promises'
import { fulfillLearn, runDayAcceptanceBrowser, runDayBrowser, runLearnerBrowser } from './learnBrowser.mjs'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'
import { runDayPanelBrowser } from './dayPanelBrowser.mjs'

let swOverride
const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true }, plugins: [{
  name: 'test-update-response',
  configurePreviewServer(server) {
    server.middlewares.use((req, res, next) => {
      // Match production's protected .html routes: precache holds no app document (not even public /login), only neutral .htm.
      if (req.url === '/guarded-navigation' || req.url === '/api/raw401') { res.statusCode = 401; res.setHeader('Content-Type', 'text/plain'); res.end('Unauthorized test response') }
      else if (req.url === '/index.html' || req.url === '/offline.html') { res.statusCode = 302; res.setHeader('Location', '/login'); res.end() }
      else if (req.url?.startsWith('/sw.js') && swOverride) { res.setHeader('Content-Type', 'text/javascript'); res.setHeader('Cache-Control', 'no-cache'); res.end(swOverride) }
      else next()
    })
  },
}] })
let browser
async function mockSession(context) {
  const identity = { username: 'demo', role: 'owner' }
  await context.route('**/api/auth/me', route => route.fulfill({ json: identity }))
  await context.route('**/api/learn/**', route => fulfillLearn(route))
  return identity
}
try {
  browser = await chromium.launch({ headless: true })
  await runLearnerBrowser(browser)
  await runDayBrowser(browser)
  await runDayAcceptanceBrowser(browser)
  await runDayPanelBrowser(browser)
  const authContext = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' })
  const authPage = await authContext.newPage()
  let user = null
  let logoutStatus = 204
  let loginStatus = 200
  let loginGate
  let releaseMe
  const meGate = new Promise(resolve => { releaseMe = resolve })
  const authErrors = []
  authPage.on('pageerror', error => authErrors.push(error.message))
  await authContext.route('**/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (path.startsWith('/api/learn/')) return fulfillLearn(route)
    if (request.method() !== 'GET') assert.equal(request.headers()['x-palabras'], '1')
    if (path.endsWith('/me')) { await meGate; return route.fulfill({ status: user ? 200 : 401, json: user ?? { error: 'unauthorized' } }) }
    if (path.endsWith('/login')) {
      const credentials = request.postDataJSON()
      if (loginGate) await loginGate
      if (loginStatus === 0) return route.abort('failed')
      if (loginStatus !== 200) return route.fulfill({ status: loginStatus, json: { error: 'login' } })
      if (credentials.password !== 'correct') return route.fulfill({ status: 401, json: { error: 'unauthorized' } })
      user = { username: credentials.username, role: credentials.username === 'reviewer' ? 'reviewer' : 'owner' }
      return route.fulfill({ json: user })
    }
    if (logoutStatus === 204) { user = null; return route.fulfill({ status: 204 }) }
    return route.fulfill({ status: logoutStatus, json: { error: 'auth' } })
  })
  await authPage.goto(`${testBaseUrl}/revisar`)
  await authPage.locator('.auth-skeleton').waitFor()
  assert.equal(await authPage.getByLabel('Usuario', { exact: true }).count(), 0)
  releaseMe()
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(new URL(authPage.url()).searchParams.get('next'), '/revisar')
  assert.equal(await authPage.getByLabel('Usuario', { exact: true }).evaluate(el => el === document.activeElement), true)
  await authPage.getByLabel('Usuario', { exact: true }).fill('learner')
  await authPage.getByLabel('Contraseña', { exact: true }).fill('wrong')
  await authPage.getByLabel('Contraseña', { exact: true }).press('Enter')
  await authPage.getByText('Usuario o contraseña incorrectos.').waitFor()
  assert.equal(await authPage.getByLabel('Usuario', { exact: true }).inputValue(), 'learner')
  assert.equal(await authPage.getByLabel('Contraseña', { exact: true }).inputValue(), '')
  for (const status of [429, 500, 0]) {
    loginStatus = status
    let releaseLogin
    if (status === 429) loginGate = new Promise(resolve => { releaseLogin = resolve })
    await authPage.getByLabel('Contraseña', { exact: true }).fill('correct')
    await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
    if (releaseLogin) {
      assert.equal(await authPage.getByRole('button', { name: 'Iniciando sesión…' }).isDisabled(), true)
      releaseLogin()
      loginGate = undefined
    }
    await authPage.getByText(status === 429 ? 'Espera un momento y vuelve a intentarlo.' : 'No se ha podido iniciar sesión. Comprueba la conexión e inténtalo de nuevo.').waitFor()
    assert.equal(await authPage.getByLabel('Usuario', { exact: true }).inputValue(), 'learner')
    assert.equal(await authPage.getByLabel('Contraseña', { exact: true }).inputValue(), '')
  }
  loginStatus = 200
  await authPage.getByLabel('Contraseña', { exact: true }).fill('correct')
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
  await authPage.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  assert.equal(new URL(authPage.url()).pathname, '/')
  await authPage.goto(`${testBaseUrl}/revisar`)
  await authPage.getByRole('heading', { name: 'Sin permiso', exact: true }).waitFor()
  assert.equal(new URL(authPage.url()).pathname, '/revisar')
  assert.equal(await authPage.getByRole('link', { name: 'Vista revisora' }).count(), 0)
  const assertLearnerNav = async () => {
    assert.equal(await authPage.getByRole('navigation', { name: 'Navegación alumno', exact: true }).count(), 1)
    assert.equal(await authPage.getByRole('navigation', { name: 'Navegación revisora', exact: true }).count(), 0)
    assert.equal(await authPage.getByRole('link', { name: 'Revisar', exact: true }).count(), 0)
    assert.equal(await authPage.getByRole('link', { name: 'Aprobadas', exact: true }).count(), 0)
  }
  await assertLearnerNav()
  await authPage.goto(`${testBaseUrl}/aprobadas`)
  await authPage.getByRole('heading', { name: 'Sin permiso', exact: true }).waitFor()
  await assertLearnerNav()
  await authPage.getByRole('button', { name: 'Volver a Hoy' }).click()
  await authPage.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  await assertLearnerNav()
  logoutStatus = 403
  await authPage.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
  await authPage.getByText('No se ha podido cerrar la sesión. Inténtalo de nuevo.').waitFor()
  assert.equal(new URL(authPage.url()).pathname, '/')
  assert.equal(await authPage.getByRole('button', { name: 'Mostrar respuesta' }).isVisible(), true)
  logoutStatus = 401
  user = null
  await authPage.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  await authPage.getByLabel('Usuario', { exact: true }).fill('reviewer')
  await authPage.getByLabel('Contraseña', { exact: true }).fill('correct')
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
  await authPage.getByRole('button', { name: 'No ✗', exact: true }).first().waitFor()
  assert.equal(new URL(authPage.url()).pathname, '/revisar')
  assert.equal(await authPage.locator('.account-name').textContent(), 'reviewer')
  assert.equal(await authPage.getByRole('navigation', { name: 'Navegación revisora', exact: true }).count(), 1)
  assert.equal(await authPage.getByRole('link', { name: 'Revisar', exact: true }).count(), 1)
  logoutStatus = 204
  await authPage.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.deepEqual(authErrors, [])
  console.log('Built auth browser: /me 401 → login; skeleton without form flash; username autofocus; Enter submit; distinct 401/429/500/network failures; username retained, password cleared; owner home, reviewer automatic /revisar; logout 403 retains session, later API 401 → login; mutation headers verified.')
  await authContext.close()
  const cachedAuthContext = await browser.newContext({ serviceWorkers: 'allow' })
  await cachedAuthContext.route('**/api/auth/me', route => route.fulfill({ status: 401, json: { error: 'unauthorized' } }))
  const cachedAuthPage = await cachedAuthContext.newPage()
  await cachedAuthPage.goto(`${testBaseUrl}/`)
  await cachedAuthPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  await cachedAuthPage.evaluate(async () => { await navigator.serviceWorker.ready; if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })) })
  await cachedAuthPage.reload()
  await cachedAuthPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(await cachedAuthPage.getByRole('button', { name: 'Mostrar respuesta' }).count(), 0)
  const cachedPaths = await cachedAuthPage.evaluate(async () => (await Promise.all((await caches.keys()).map(async name => (await (await caches.open(name)).keys()).map(request => new URL(request.url).pathname)))).flat())
  assert.ok(!cachedPaths.includes('/login'))
  assert.ok(cachedPaths.includes('/offline.htm'))
  assert.ok(!cachedPaths.includes('/index.html'))
  assert.ok(cachedPaths.every(path => !path.startsWith('/api')))
  const rawApi = await cachedAuthPage.evaluate(async () => { const response = await fetch('/api/raw401'); return { status: response.status, body: await response.text() } })
  assert.deepEqual(rawApi, { status: 401, body: 'Unauthorized test response' })
  await cachedAuthContext.route('**/api/auth/me', route => route.fulfill({ json: { username: 'demo', role: 'owner' } }))
  const guarded = await cachedAuthPage.goto(`${testBaseUrl}/guarded-navigation`)
  assert.equal(guarded.status(), 401)
  assert.equal(await cachedAuthPage.locator('body').textContent(), 'Unauthorized test response')
  await cachedAuthContext.setOffline(true)
  const offlineApi = await cachedAuthPage.evaluate(async () => { try { await fetch('/api/raw401'); return 'masked' } catch { return 'network failure' } })
  assert.equal(offlineApi, 'network failure')
  console.log(`Built bundle with active SW, no cached app document: /me 401 → /login, no learner view; ${cachedPaths.length} static cached paths, no API responses.`)
  console.log('Active SW: raw API and navigation 401 preserved with original body; offline API rejects, never receives fallback HTML. Protected .html precache URLs avoided.')
  await cachedAuthContext.close()
  for (const viewport of [{ width: 390, height: 844 }, { width: 2000, height: 1200 }]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' })
    const demoIdentity = await mockSession(context)
    const page = await context.newPage()
    const errors = []; const apiRequests = []
    page.on('pageerror', error => errors.push(error.message))
    page.on('request', req => { const pathname = new URL(req.url()).pathname; if (pathname.startsWith('/api/')) apiRequests.push({ method: req.method(), pathname }) })
    await page.goto(`${testBaseUrl}/`)
    await page.evaluate(async () => { await navigator.serviceWorker.ready; if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })) })
    const measure = async () => page.evaluate(() => ({
      viewport: innerWidth, scroll: document.documentElement.scrollWidth,
      shell: Math.round(document.querySelector('.app-shell').getBoundingClientRect().width),
      cardWidth: document.querySelector('.flashcard') ? Math.round(document.querySelector('.flashcard').getBoundingClientRect().width) : null,
      columns: getComputedStyle(document.querySelector('.learner-layout') ?? document.querySelector('.review-grid')).gridTemplateColumns,
      smallTargets: [...document.querySelectorAll('button, .bottom-nav a')].filter(el => el.getBoundingClientRect().width && (el.getBoundingClientRect().height < 44 || el.getBoundingClientRect().width < 44)).length,
    }))
    await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
    const learner = await measure()
    assert.ok(learner.scroll <= viewport.width); assert.ok(learner.shell <= 1600); assert.ok(learner.cardWidth <= 672); assert.equal(learner.smallTargets, 0)
    if (viewport.width >= 1280) assert.equal(await page.locator('.side-list').isVisible(), true)
    await page.getByRole('button', { name: 'Mostrar respuesta' }).click()
    await assert.doesNotReject(() => page.locator('.answer h3').waitFor())
    assert.equal(await page.locator('.learn-session').evaluate(el => el === document.activeElement), true)
    const grading = await measure(); assert.equal(grading.smallTargets, 0)
    if (viewport.width < 768) {
      const action = await page.locator('.session-actions').boundingBox()
      const nav = await page.locator('.bottom-nav').boundingBox()
      assert.ok(action.y + action.height <= nav.y, 'grading above bottom navigation')
      assert.ok(action.y + action.height > viewport.height * .5, 'grading thumb reachable')
    }
    await page.getByRole('button', { name: 'Good La sabía' }).click()
    await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
    await page.waitForFunction(() => document.activeElement === document.querySelector('.learn-session'))
    assert.equal(await page.locator('.learn-session').evaluate(el => el === document.activeElement), true)
    demoIdentity.role = 'reviewer'
    await page.goto(`${testBaseUrl}/`)
    await page.getByRole('button', { name: 'No ✗', exact: true }).first().waitFor()
    const reviewer = await measure(); assert.ok(reviewer.scroll <= viewport.width); assert.equal(reviewer.smallTargets, 0)
    assert.equal(reviewer.columns.split(' ').length, viewport.width >= 1536 ? 3 : 1)
    await page.getByRole('button', { name: 'No ✗', exact: true }).first().click()
    await page.locator('dialog[open]').waitFor()
    assert.equal(await page.getByRole('button', { name: 'Cerrar alternativas' }).evaluate(el => el === document.activeElement), true)
    const drawer = await page.locator('dialog').boundingBox()
    if (viewport.width >= 1280) assert.ok(drawer.x >= viewport.width - 600)
    await page.getByRole('button', { name: /potatoes \(as a dish\)/ }).click()
    await page.getByRole('button', { name: 'Editar ✎', exact: true }).first().click()
    await page.getByLabel('Traducción', { exact: true }).fill('mid-morning snack')
    await page.getByRole('button', { name: 'Guardar y aprobar' }).click()
    assert.ok(await page.getByRole('status').first().textContent())
    // Real browser text scaling and scheme; no visual claim based on metrics.
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
    const scaled = await measure(); assert.ok(scaled.scroll <= viewport.width); assert.equal(scaled.smallTargets, 0)
    await page.emulateMedia({ colorScheme: 'dark' })
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), 'dark')
    await context.setOffline(true)
    await page.goto(`${testBaseUrl}/palabras`)
    await page.getByRole('heading', { name: 'Sin conexión' }).waitFor()
    await page.getByText('No se puede confirmar si has iniciado sesión. Conéctate para abrir Palabras.').waitFor()
    await page.goto(`${testBaseUrl}/unknown-offline`)
    await page.getByRole('heading', { name: 'Sin conexión' }).waitFor()
    assert.deepEqual(errors, [])
    // Two online app loads; both offline navigations serve signed-out-neutral fallback.
    assert.equal(apiRequests.filter(request => request.pathname === '/api/auth/me').length, 2)
    assert.ok(apiRequests.every(request => request.pathname.startsWith('/api/learn/') || (request.method === 'GET' && request.pathname === '/api/auth/me')))
    console.log(JSON.stringify({ viewport, learner, grading, reviewer, drawer, textScale200: scaled, offlineNavigation: 'neutral fallback', revealFocus: 'ok', editAlternative: 'ok', pageErrors: errors, apiRequests }))
    await context.close()
  }
  const updateContext = await browser.newContext()
  await mockSession(updateContext)
  const updatePage = await updateContext.newPage()
  await updatePage.goto(`${testBaseUrl}/`)
  await updatePage.evaluate(async () => { await navigator.serviceWorker.ready; if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })) })
  await updatePage.getByRole('button', { name: 'Mostrar respuesta' }).click()
  swOverride = (await readFile(new URL('../dist/sw.js', import.meta.url), 'utf8')).replace(/palabras-shell-[a-f0-9]+/, 'palabras-shell-browser-update')
  await Promise.all([updatePage.waitForEvent('load'), updatePage.evaluate(() => window.dispatchEvent(new Event('focus')))])
  await updatePage.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  assert.equal(await updatePage.getByRole('button', { name: 'Actualizar y reiniciar' }).count(), 0)
  console.log('Update flow: focus discovers changed worker without manual update/cache clear; atomic install activates and reloads shell once.')
  await updateContext.close()
  swOverride = undefined
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  await mockSession(context)
  const page = await context.newPage()
  await page.addInitScript(() => { ServiceWorkerContainer.prototype.register = () => Promise.reject(new Error('Simulated registration failure')) })
  await page.goto(`${testBaseUrl}/`)
  await page.getByRole('link', { name: 'Ajustes', exact: true }).click()
  await page.getByText('Registro fallido; la app sigue funcionando en línea').waitFor()
  console.log('Registration failure: app/settings usable, auth backend mocked.')
  const response = await page.request.get(`${testBaseUrl}/manifest.webmanifest`)
  assert.equal(response.status(), 200)
  assert.equal(response.headers()['cache-control'], 'no-cache')
  const manifest = await response.json()
  for (const icon of manifest.icons) assert.equal((await page.request.get(`${testBaseUrl}${icon.src}`)).status(), 200)
  const sw = await page.request.get(`${testBaseUrl}/sw.js`)
  assert.equal(sw.status(), 200)
  assert.equal(sw.headers()['cache-control'], 'no-cache')
  console.log('Built preview public routes: manifest, all 4 icons and sw.js return 200; manifest/sw Cache-Control: no-cache. Production host not verified.')
  await context.close()
  // preview sets NODE_ENV=production; reset before deliberately starting dev harness.
  process.env.NODE_ENV = 'development'
  const devServer = await createServer({ mode: 'development', server: { host: '127.0.0.1', port: 5173, strictPort: true } })
  await devServer.listen()
  try {
    const raceContext = await browser.newContext({ serviceWorkers: 'block' })
    const racePage = await raceContext.newPage()
    const raceErrors = []
    racePage.on('pageerror', error => raceErrors.push(error.message))
    let captureMe
    const pendingMe = new Promise(resolve => { captureMe = resolve })
    await raceContext.route('**/api/auth/me', route => captureMe(route))
    await raceContext.route('**/api/guard-test', route => route.fulfill({ status: 401, json: { error: 'unauthorized' } }))
    await racePage.goto('http://127.0.0.1:5173/revisar')
    const staleMe = await pendingMe
    await racePage.getByText('Comprobando sesión…', { exact: true }).waitFor()
    await racePage.evaluate(async () => {
      const { apiRequest, ApiError } = await import('/src/api/client.ts')
      try { await apiRequest('/api/guard-test') } catch (error) {
        if (!(error instanceof ApiError && error.kind === 'unauthorized')) throw error
      }
    })
    await racePage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
    const staleResponse = racePage.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/me')
    await staleMe.fulfill({ json: { username: 'stale-reviewer', role: 'reviewer' } })
    assert.equal(await (await staleResponse).finished(), null)
    // Flush response processing and React's browser render before checking absence of restoration.
    await racePage.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    assert.equal(new URL(racePage.url()).pathname, '/login')
    assert.equal(await racePage.getByRole('button', { name: 'Iniciar sesión', exact: true }).isVisible(), true)
    assert.equal(await racePage.getByRole('button', { name: 'Cerrar sesión', exact: true }).count(), 0)
    assert.deepEqual(raceErrors, [])
    console.log('Session revision guard: delayed successful /me cannot restore session after a later API 401.')
    await raceContext.close()
    const debugContext = await browser.newContext({ viewport: { width: 390, height: 844 } })
    await debugContext.route('**/api/auth/me', route => route.fulfill({ status: 401, json: { error: 'unauthorized' } }))
    const debugPage = await debugContext.newPage()
    await debugPage.goto('http://127.0.0.1:5173/debug/viewport')
    await debugPage.getByRole('heading', { name: 'Diagnóstico en vivo · solo desarrollo' }).waitFor()
    await debugPage.getByText('390 × 844 CSS px', { exact: true }).waitFor()
    await debugPage.getByText(/Manifest ✓/).waitFor()
    await debugPage.setViewportSize({ width: 2000, height: 1200 })
    await debugPage.getByText('2000 × 1200 CSS px', { exact: true }).waitFor()
    await debugPage.getByText('2xl', { exact: true }).waitFor()
    console.log('Anonymous dev viewport route with /me 401: live resize 390×844 → 2000×1200; breakpoint 2xl; manifest/icons decoded; no backend.')
    await debugContext.close()
  } finally { await devServer.close() }
  const files = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8')
  assert.ok(files.includes('manifest.webmanifest'))
  console.log('Browser verification complete.')
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
