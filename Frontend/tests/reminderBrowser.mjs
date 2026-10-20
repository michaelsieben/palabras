// Mocked SPA permission/state integration. No real dialog, push service or VAPID exchange.
import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

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
    return route.fulfill({ json: path === '/api/auth/me' ? { username: 'owner', role: 'owner' } : path === '/api/learn/reminder' ? { enabled: true, time: '22:00', mutedUntil: null, windowMin: 90, activeDevices: 0 } : { localDate: '2026-10-20', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null } })
  })
  const page = await context.newPage(); await page.goto(testBaseUrl + '/ajustes')
  await page.getByText('Recordatorios no disponibles:', { exact: false }).waitFor()
  assert.doesNotMatch(await page.locator('main').innerText(), /0 dispositivos pueden/)
  assert.equal(await page.getByRole('button', { name: 'Enviar prueba' }).isDisabled(), true)
  await context.close()
  console.log('Reminder browser assertions passed (mocked permissions/API; no real push).')
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
