import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { createServer, preview } from 'vite'
import { readFile } from 'node:fs/promises'

let swOverride
const server = await preview({ preview: { host: '127.0.0.1', port: 4173, strictPort: true }, plugins: [{
  name: 'test-update-response',
  configurePreviewServer(server) {
    server.middlewares.use((req, res, next) => {
      if (req.url?.startsWith('/sw.js') && swOverride) { res.setHeader('Content-Type', 'text/javascript'); res.setHeader('Cache-Control', 'no-cache'); res.end(swOverride) }
      else next()
    })
  },
}] })
let browser
async function mockSession(context) {
  await context.route('**/api/auth/me', route => route.fulfill({ json: { username: 'demo', roles: ['learner', 'reviewer'] } }))
}
try {
  browser = await chromium.launch({ headless: true })
  const authContext = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' })
  const authPage = await authContext.newPage()
  let user = null
  let logoutStatus = 204
  const authErrors = []
  authPage.on('pageerror', error => authErrors.push(error.message))
  await authContext.route('**/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    if (request.method() !== 'GET') assert.equal(request.headers()['x-palabras'], '1')
    if (path.endsWith('/me')) return route.fulfill({ status: user ? 200 : 401, json: user ?? { error: 'unauthorized' } })
    if (path.endsWith('/login')) {
      const credentials = request.postDataJSON()
      if (credentials.password !== 'correct') return route.fulfill({ status: 401, json: { error: 'unauthorized' } })
      user = { username: credentials.username, roles: [credentials.username === 'reviewer' ? 'reviewer' : 'learner'] }
      return route.fulfill({ json: user })
    }
    if (logoutStatus === 204) { user = null; return route.fulfill({ status: 204 }) }
    return route.fulfill({ status: logoutStatus, json: { error: 'auth' } })
  })
  await authPage.goto('http://127.0.0.1:4173/revisar')
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.equal(new URL(authPage.url()).searchParams.get('next'), '/revisar')
  await authPage.getByLabel('Usuario', { exact: true }).fill('learner')
  await authPage.getByLabel('Contraseña', { exact: true }).fill('wrong')
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
  await authPage.getByText('Usuario o contraseña incorrectos.').waitFor()
  await authPage.getByLabel('Contraseña', { exact: true }).fill('correct')
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
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
  await authPage.goto('http://127.0.0.1:4173/aprobadas')
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
  await authPage.getByRole('link', { name: 'Vista revisora' }).click()
  await authPage.getByRole('button', { name: 'No ✗', exact: true }).first().waitFor()
  assert.equal(await authPage.getByRole('navigation', { name: 'Navegación revisora', exact: true }).count(), 1)
  assert.equal(await authPage.getByRole('link', { name: 'Revisar', exact: true }).count(), 1)
  logoutStatus = 204
  await authPage.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
  await authPage.getByRole('button', { name: 'Iniciar sesión', exact: true }).waitFor()
  assert.deepEqual(authErrors, [])
  console.log('Auth browser: anonymous intended route, bad credentials, learner denial, reviewer access, logout; 403 retains session, 401 redirects; mutation headers verified.')
  await authContext.close()
  for (const viewport of [{ width: 390, height: 844 }, { width: 2000, height: 1200 }]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' })
    await mockSession(context)
    const page = await context.newPage()
    const errors = []; const apiRequests = []
    page.on('pageerror', error => errors.push(error.message))
    page.on('request', req => { const pathname = new URL(req.url()).pathname; if (pathname.startsWith('/api/')) apiRequests.push({ method: req.method(), pathname }) })
    await page.goto('http://127.0.0.1:4173/')
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
    assert.equal(await page.locator('.answer h3').evaluate(el => el === document.activeElement), true)
    const grading = await measure(); assert.equal(grading.smallTargets, 0)
    if (viewport.width < 768) {
      const action = await page.locator('.session-actions').boundingBox()
      const nav = await page.locator('.bottom-nav').boundingBox()
      assert.ok(action.y + action.height <= nav.y, 'grading above bottom navigation')
      assert.ok(action.y + action.height > viewport.height * .5, 'grading thumb reachable')
    }
    await page.getByRole('button', { name: 'Good La sabía' }).click()
    assert.equal(await page.getByRole('button', { name: 'Mostrar respuesta' }).evaluate(el => el === document.activeElement), true)
    await page.getByRole('link', { name: 'Vista revisora' }).click()
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
    // Playwright still fulfils mocked /me while offline: shell coverage, not offline-session coverage.
    await page.goto('http://127.0.0.1:4173/palabras')
    await page.getByRole('heading', { name: 'Tus palabras' }).waitFor()
    await page.goto('http://127.0.0.1:4173/unknown-offline')
    await page.getByRole('heading', { name: 'Sin conexión' }).waitFor()
    assert.deepEqual(errors, [])
    // Two app loads (home and palabras); unknown-offline serves the fallback, not the app.
    assert.equal(apiRequests.length, 2)
    assert.ok(apiRequests.every(request => request.method === 'GET' && request.pathname === '/api/auth/me'))
    console.log(JSON.stringify({ viewport, learner, grading, reviewer, drawer, textScale200: scaled, offlineShell: 'ok', offlineFallback: 'ok', revealFocus: 'ok', editAlternative: 'ok', pageErrors: errors, apiRequests }))
    await context.close()
  }
  const updateContext = await browser.newContext()
  await mockSession(updateContext)
  const updatePage = await updateContext.newPage()
  await updatePage.goto('http://127.0.0.1:4173/')
  await updatePage.evaluate(async () => { await navigator.serviceWorker.ready; if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true })) })
  await updatePage.getByRole('button', { name: 'Mostrar respuesta' }).click()
  swOverride = (await readFile(new URL('../dist/sw.js', import.meta.url), 'utf8')).replace(/palabras-shell-[a-f0-9]+/, 'palabras-shell-browser-update')
  await updatePage.evaluate(async () => { const reg = await navigator.serviceWorker.getRegistration(); await reg.update() })
  await updatePage.getByRole('button', { name: 'Actualizar y reiniciar' }).waitFor()
  assert.equal(await updatePage.locator('.answer h3').isVisible(), true, 'update does not discard current card before confirmation')
  await Promise.all([updatePage.waitForEvent('load'), updatePage.getByRole('button', { name: 'Actualizar y reiniciar' }).click()])
  await updatePage.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
  assert.equal(await updatePage.getByRole('button', { name: 'Actualizar y reiniciar' }).count(), 0)
  console.log('Update flow: changed worker waits, current answer preserved; explicit refresh activates new worker and reloads shell.')
  await updateContext.close()
  swOverride = undefined
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  await mockSession(context)
  const page = await context.newPage()
  await page.addInitScript(() => { ServiceWorkerContainer.prototype.register = () => Promise.reject(new Error('Simulated registration failure')) })
  await page.goto('http://127.0.0.1:4173/')
  await page.getByRole('link', { name: 'Ajustes', exact: true }).click()
  await page.getByText('Registro fallido; la app sigue funcionando en línea').waitFor()
  console.log('Registration failure: app/settings usable, auth backend mocked.')
  const response = await page.request.get('http://127.0.0.1:4173/manifest.webmanifest')
  assert.equal(response.headers()['cache-control'], 'no-cache')
  const sw = await page.request.get('http://127.0.0.1:4173/sw.js')
  assert.equal(sw.headers()['cache-control'], 'no-cache')
  console.log('Vite preview app headers: manifest.webmanifest and sw.js Cache-Control: no-cache.')
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
    await staleMe.fulfill({ json: { username: 'stale-reviewer', roles: ['reviewer'] } })
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
    await mockSession(debugContext)
    const debugPage = await debugContext.newPage()
    await debugPage.goto('http://127.0.0.1:5173/debug/viewport')
    await debugPage.getByRole('heading', { name: 'Diagnóstico en vivo · solo desarrollo' }).waitFor()
    await debugPage.getByText('390 × 844 CSS px', { exact: true }).waitFor()
    await debugPage.getByText(/Manifest ✓/).waitFor()
    await debugPage.setViewportSize({ width: 2000, height: 1200 })
    await debugPage.getByText('2000 × 1200 CSS px', { exact: true }).waitFor()
    await debugPage.getByText('2xl', { exact: true }).waitFor()
    console.log('Dev viewport route: live resize 390×844 → 2000×1200; breakpoint 2xl; manifest/icons decoded; no backend.')
    await debugContext.close()
  } finally { await devServer.close() }
  const files = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8')
  assert.ok(files.includes('manifest.webmanifest'))
  console.log('Browser verification complete.')
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
