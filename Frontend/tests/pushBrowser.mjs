// Built-worker integration. The push event here is synthetic: it is injected through CDP
// (ServiceWorker.deliverPushMessage). No real push service and no PushManager subscription are
// involved, so a pass proves the built worker's handlers run, not that a service delivered a push.
import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
// Playwright resolves waitForFunction on the truthy Promise that an async predicate returns, so
// the notification poll is awaited here instead of inside the page.
async function waitFor(description, check, timeout = 15000) {
  const deadline = Date.now() + timeout
  for (;;) {
    if (await check()) return
    if (Date.now() > deadline) throw new Error('timed out waiting for ' + description)
    await new Promise(resolve => setTimeout(resolve, 100))
  }
}
let browser
try {
  // The image's default headless Shell leaves Notification.permission at 'denied' even after
  // grantPermissions, and a denied worker silently drops showNotification; the bundled full
  // Chromium in new-headless mode grants it, so the harness uses that browser explicitly.
  browser = await chromium.launch({ headless: true, channel: 'chromium' })
  const context = await browser.newContext({ serviceWorkers: 'allow' })
  await context.grantPermissions(['notifications'])
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
  assert.equal(await page.evaluate(() => Notification.permission), 'granted', 'harness granted the notification permission')
  // The worker the page just registered is the built artifact, not a stub: fetch it past the
  // worker and require all three U7 listeners in the served bytes.
  const served = await (await page.request.get(testBaseUrl + '/sw.js')).text()
  for (const listener of ['addEventListener("push"', 'addEventListener("notificationclick"', 'addEventListener("pushsubscriptionchange"']) {
    assert.ok(served.includes(listener), 'served sw.js contains ' + listener)
  }
  const payload = { v: 1, kind: 'daily', title: 'Built worker reminder', body: 'Backend body', localDate: '2026-10-20', expiresAt: '2099-10-20T21:30:00Z', streak: 12, skipAvailable: true, url: '/?start=1' }
  const deliver = async value => cdp.send('ServiceWorker.deliverPushMessage', { origin: testBaseUrl, registrationId, data: JSON.stringify(value) })
  const notifications = () => page.evaluate(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).map(notification => ({ title: notification.title, body: notification.body, tag: notification.tag, data: notification.data, silent: notification.silent })))
  await deliver(payload)
  await waitFor('the daily push to display its backend title', async () => (await notifications()).some(notification => notification.title === 'Built worker reminder'))
  let shown = await notifications()
  assert.equal(shown.length, 1); assert.equal(shown[0].body, 'Backend body')
  assert.equal(shown[0].data.url, testBaseUrl + '/?start=1')
  await deliver({ ...payload, title: 'Replacement reminder' })
  await waitFor('the replacement push to display', async () => (await notifications()).some(notification => notification.title === 'Replacement reminder'))
  shown = await notifications(); assert.equal(shown.length, 1, 'stable tag replaces rather than stacks')
  await deliver({ ...payload, expiresAt: '2000-01-01T00:00:00Z' })
  await waitFor('the expired push to fall back to the silent copy', async () => (await notifications()).some(notification => notification.title === 'Repaso pendiente'))
  shown = await notifications(); assert.equal(shown.length, 1); assert.equal(shown[0].silent, true)
  await page.evaluate(async () => { for (const notification of await (await navigator.serviceWorker.ready).getNotifications()) notification.close() })
  console.log('PUSH BROWSER: built sw.js, synthetic CDP push delivery: backend title/body displayed, daily tag replaces instead of stacking, expired payload silent; no subscription UI, no real push service.')
  await context.close()
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
