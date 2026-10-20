// Mocked SPA permission/state integration. No real dialog, push service or VAPID exchange.
import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

const deviceRows = () => ['active', 'active', 'stale_key', 'rejected'].map((state, i) => ({ id: 42 + i, deviceLabel: i === 0 ? 'Mi Chrome' : null, state, lastStatus: state === 'rejected' ? 410 : null, createdAtMs: 1, updatedAtMs: 2, isCurrent: false }))
const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
let browser
try {
  browser = await chromium.launch({ headless: true })
  for (const permission of ['default', 'granted', 'denied']) {
    const writes = []
    const context = await browser.newContext({ serviceWorkers: 'block' })
    await context.addInitScript(({ permission }) => {
      window.__permissionCalls = 0
      Object.defineProperty(Notification, 'permission', { configurable: true, get: () => permission })
      Notification.requestPermission = async () => { window.__permissionCalls++; return 'denied' }
    }, { permission })
    await context.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (path === '/api/learn/reminder' && route.request().method() === 'PUT') writes.push(route.request().postDataJSON())
      const body = path === '/api/auth/me' ? { username: 'owner', role: 'owner' }
        : path === '/api/push/config' ? { publicKey: 'key', keyId: 'id' }
          : path === '/api/push/subscriptions' ? { items: deviceRows(), truncated: false }
          : path === '/api/learn/reminder' ? { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 0 }
            : { localDate: '2026-10-20', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null }
      await route.fulfill({ json: body })
    })
    const page = await context.newPage()
    await page.goto(testBaseUrl + '/ajustes')
    await page.getByRole('heading', { name: 'Recordatorio de repaso' }).waitFor()
    await page.getByRole('button', { name: 'Guardar preferencias' }).waitFor()
    assert.equal(await page.evaluate(() => window.__permissionCalls), 0, 'opening settings never prompts')
    if (permission === 'default') {
      await page.getByRole('button', { name: 'Activar recordatorio' }).click()
      assert.equal(await page.evaluate(() => window.__permissionCalls), 1)
    } else if (permission === 'denied') {
      assert.equal(await page.getByRole('button', { name: 'Activar recordatorio' }).count(), 0)
      assert.match(await page.locator('main').innerText(), /No volveremos a pedir permiso/)
    } else assert.match(await page.locator('main').innerText(), /aún no está suscrito/)
    await page.getByRole('button', { name: 'Silenciar o reanudar' }).click()
    await page.getByRole('heading', { name: 'Silenciar el recordatorio' }).waitFor()
    const applied = page.waitForResponse(response => new URL(response.url()).pathname === '/api/learn/reminder' && response.request().method() === 'PUT')
    await page.getByRole('button', { name: 'Cancelar silencio y reanudar' }).click()
    await applied
    assert.deepEqual(writes, [{ muteChoice: 'cancel' }], 'cancel one exclusive durable mutation')
    await context.close()
  }
  const context = await browser.newContext({ serviceWorkers: 'block' })
  await context.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/push/config') return route.fulfill({ status: 503, json: { error: 'push_disabled' } })
    if (path === '/api/push/subscriptions') return route.fulfill({ json: { items: deviceRows(), truncated: false } })
    return route.fulfill({ json: path === '/api/auth/me' ? { username: 'owner', role: 'owner' } : path === '/api/learn/reminder' ? { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 0 } : { localDate: '2026-10-20', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null } })
  })
  const page = await context.newPage(); await page.goto(testBaseUrl + '/ajustes')
  await page.getByText('Recordatorios no disponibles:', { exact: false }).waitFor()
  assert.doesNotMatch(await page.locator('main').innerText(), /0 dispositivos pueden/)
  assert.equal(await page.getByRole('button', { name: 'Enviar prueba' }).isDisabled(), true)
  assert.equal(await page.getByRole('button', { name: /^Desactivar dispositivo/ }).count(), 0)
  assert.match(await page.locator('.reminder-devices').innerText(), /Necesita volver a suscribirse/)
  await context.close()

  // Full mocked device lifecycle: no actual browser permission or push delivery proof.
  const management = await browser.newContext({ serviceWorkers: 'block' })
  const publicKey = Buffer.from([4, ...Array.from({ length: 64 }, (_, i) => i)]).toString('base64url')
  await management.addInitScript(({ publicKey }) => {
    let sequence = 0
    window.__unsubscribes = 0
    Object.defineProperty(Notification, 'permission', { configurable: true, get: () => 'granted' })
    let subscription
    const make = () => ({ endpoint: `https://push.test/device-${sequence}`, options: { applicationServerKey: Uint8Array.from(atob(publicKey.replaceAll('-', '+').replaceAll('_', '/')), c => c.charCodeAt(0)).buffer }, toJSON: () => ({ endpoint: `https://push.test/device-${sequence}`, keys: { p256dh: 'p', auth: 'a' } }), unsubscribe: async () => { window.__unsubscribes++; subscription = null; return true } })
    subscription = make()
    navigator.serviceWorker.getRegistration = async () => ({ active: true, pushManager: { getSubscription: async () => subscription, subscribe: async () => { sequence++; subscription = make(); return subscription } }, getNotifications: async () => [] })
  }, { publicKey })
  const rows = deviceRows(); const requests = []; let revokedSilently = false
  await management.route('**/api/**', async route => {
    const request = route.request(); const url = new URL(request.url()); const path = url.pathname
    requests.push({ path, method: request.method(), currentId: url.searchParams.get('currentId') })
    if (path === '/api/push/subscription' && request.method() === 'PUT') {
      const body = request.postDataJSON()
      if (revokedSilently && !body.reactivate) return route.fulfill({ status: 409, json: { error: 'revoked' } })
      const id = body.endpoint.endsWith('device-0') ? 42 : 46
      if (!rows.find(row => row.id === id)) rows.push({ ...rows[0], id, state: 'active' })
      rows.find(row => row.id === id).state = 'active'
      return route.fulfill({ json: { id, state: 'active' } })
    }
    if (path.startsWith('/api/push/subscriptions/') && request.method() === 'DELETE') {
      assert.equal(request.headers()['x-palabras'], '1')
      const row = rows.find(row => row.id === Number(path.split('/').at(-1)))
      row.state = 'rejected'; row.lastStatus = null
      return route.fulfill({ status: 204 })
    }
    if (path === '/api/push/subscriptions') return route.fulfill({ json: { items: rows.map(row => ({ ...row, isCurrent: row.id === Number(url.searchParams.get('currentId')) })), truncated: true } })
    return route.fulfill({ json: path === '/api/auth/me' ? { username: 'owner', role: 'owner' } : path === '/api/push/config' ? { publicKey, keyId: 'id' } : path === '/api/learn/reminder' ? { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 2 } : { localDate: '2026-10-20', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null } })
  })
  const managedPage = await management.newPage(); await managedPage.goto(testBaseUrl + '/ajustes')
  await managedPage.getByRole('button', { name: 'Desactivar dispositivo 42 (este dispositivo)', exact: true }).waitFor()
  assert.match(await managedPage.locator('.reminder-devices').innerText(), /Necesita volver a suscribirse.*Rechazado/s)
  await managedPage.getByText('Se muestran los primeros 50 dispositivos.', { exact: false }).waitFor()
  await managedPage.getByRole('button', { name: 'Desactivar dispositivo 43', exact: true }).click()
  await managedPage.waitForFunction(() => [...document.querySelectorAll('.reminder-devices li')].some(row => row.querySelector('button')?.getAttribute('aria-label') === 'Desactivar dispositivo 43' && row.textContent.includes('Desactivado')))
  assert.equal(await managedPage.evaluate(() => window.__unsubscribes), 0)
  const deleteIndex = requests.findIndex(r => r.path === '/api/push/subscriptions/43')
  assert.ok(requests.slice(deleteIndex + 1).some(r => r.path === '/api/push/subscriptions' && r.currentId === '42'), '204 is followed by list refetch with acknowledged id')
  // A server-side revoke on another client is discovered silently on app open.
  revokedSilently = true; rows[0].state = 'rejected'; rows[0].lastStatus = null
  await managedPage.reload()
  await managedPage.getByText('Has desactivado los recordatorios en este dispositivo.', { exact: false }).waitFor()
  assert.equal(await managedPage.getByRole('alert').count(), 0)
  await managedPage.getByRole('button', { name: 'Activar recordatorio', exact: true }).click()
  await managedPage.getByRole('button', { name: 'Desactivar dispositivo 42 (este dispositivo)', exact: true }).waitFor({ state: 'visible' })
  await managedPage.waitForFunction(() => document.querySelector('[aria-label="Desactivar dispositivo 42 (este dispositivo)"]')?.disabled === false)
  await managedPage.getByRole('button', { name: 'Desactivar dispositivo 42 (este dispositivo)', exact: true }).click()
  await managedPage.waitForFunction(() => window.__unsubscribes === 1)
  await managedPage.getByRole('button', { name: 'Activar recordatorio', exact: true }).click()
  await managedPage.getByRole('button', { name: 'Desactivar dispositivo 46 (este dispositivo)', exact: true }).waitFor()
  assert.ok(requests.some(r => r.path === '/api/push/subscriptions' && r.currentId === '46'), 'new endpoint gets a new row id')
  await management.close()
  console.log('Reminder browser assertions passed (mocked permissions/API; no real push).')
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
