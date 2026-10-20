// Built-worker integration; no PushManager subscription or permission prompt from app UI.
import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
let browser
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  await context.grantPermissions(['notifications'], { origin: testBaseUrl })
  const page = await context.newPage()
  await page.goto(testBaseUrl + '/offline.htm')
  const cdp = await context.newCDPSession(page)
  let registrationId
  cdp.on('ServiceWorker.workerRegistrationUpdated', ({ registrations }) => {
    registrationId = registrations.find(registration => registration.scopeURL === testBaseUrl + '/' && !registration.isDeleted)?.registrationId ?? registrationId
  })
  await cdp.send('ServiceWorker.enable')
  await page.evaluate(async () => {
    await navigator.serviceWorker.register('/sw.js', { scope: '/' })
    await navigator.serviceWorker.ready
    if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }))
  })
  assert.ok(registrationId, 'CDP observed built-worker registration')
  const payload = { v: 1, kind: 'daily', title: 'Built worker reminder', body: 'Backend body', localDate: '2026-10-20', expiresAt: '2099-10-20T21:30:00Z', streak: 12, skipAvailable: true, url: '/?start=1' }
  const deliver = async value => cdp.send('ServiceWorker.deliverPushMessage', { origin: testBaseUrl, registrationId, data: JSON.stringify(value) })
  const notifications = () => page.evaluate(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).map(notification => ({ title: notification.title, body: notification.body, tag: notification.tag, data: notification.data, silent: notification.silent })))
  await deliver(payload)
  await page.waitForFunction(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).some(notification => notification.title === 'Built worker reminder'))
  let shown = await notifications()
  assert.equal(shown.length, 1); assert.equal(shown[0].body, 'Backend body')
  assert.equal(shown[0].data.url, testBaseUrl + '/?start=1')
  await deliver({ ...payload, title: 'Replacement reminder' })
  await page.waitForFunction(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).some(notification => notification.title === 'Replacement reminder'))
  assert.equal((await notifications()).length, 1, 'stable tag replaces rather than stacks')
  await deliver({ ...payload, expiresAt: '2000-01-01T00:00:00Z' })
  await page.waitForFunction(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).some(notification => notification.title === 'Repaso pendiente'))
  shown = await notifications(); assert.equal(shown.length, 1); assert.equal(shown[0].silent, true)
  await page.evaluate(async () => { for (const notification of await (await navigator.serviceWorker.ready).getNotifications()) notification.close() })
  console.log('PUSH BROWSER: built sw.js CDP delivery displays backend title/body, replaces daily tag, expires silently; no subscription UI.')
  await context.close()
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
