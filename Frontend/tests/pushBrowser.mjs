// Built-worker integration. The push event here is synthetic: it is injected through CDP
// (ServiceWorker.deliverPushMessage). No real push service and no PushManager subscription are
// involved, so a pass proves the built worker's handlers run, not that a service delivered a push.
import assert from 'node:assert/strict'
import { chromium, expect } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'

const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
// Each wait observes its own condition; this is a failure budget, never a startup sleep
// or a retry of the push injection. Keep lifecycle and delivery failures distinguishable.
const timeout = 60000
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
  const versions = new Map()
  const dispatchedPushes = []
  const completedPushes = []
  // CDP injection acknowledges scheduling, not event.waitUntil/showNotification completion.
  // Subscribe and enable recording before injection. Reading getNotifications earlier can
  // race Chromium's platform/database synchronization and prune an in-flight notification.
  cdp.on('BackgroundService.backgroundServiceEventReceived', ({ backgroundServiceEvent: event }) => {
    if (event.service !== 'pushMessaging' || event.serviceWorkerRegistrationId !== registrationId) return
    if (event.eventName === 'Push event dispatched') dispatchedPushes.push(event)
    if (event.eventName === 'Push event completed') completedPushes.push(event)
  })
  cdp.on('ServiceWorker.workerRegistrationUpdated', ({ registrations }) => {
    registrationId = registrations.find(registration => registration.scopeURL === testBaseUrl + '/' && !registration.isDeleted)?.registrationId ?? registrationId
  })
  cdp.on('ServiceWorker.workerVersionUpdated', ({ versions: updates }) => {
    for (const version of updates) versions.set(version.versionId, version)
  })
  cdp.on('ServiceWorker.workerErrorReported', error => console.error('PUSH BROWSER worker error:', JSON.stringify(error)))
  await cdp.send('ServiceWorker.enable')
  await cdp.send('BackgroundService.startObserving', { service: 'pushMessaging' })
  await cdp.send('BackgroundService.setRecording', { service: 'pushMessaging', shouldRecord: true })
  await page.evaluate(async timeout => {
    await navigator.serviceWorker.register('/sw.js', { scope: '/' })
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { cleanup(); reject(new Error('built worker did not activate and control the page')) }, timeout)
      let worker
      const cleanup = () => {
        clearTimeout(timer)
        navigator.serviceWorker.removeEventListener('controllerchange', check)
        worker?.removeEventListener('statechange', check)
      }
      const check = () => {
        const controller = navigator.serviceWorker.controller
        if (controller !== worker) {
          worker?.removeEventListener('statechange', check)
          worker = controller
          worker?.addEventListener('statechange', check)
        }
        if (worker?.state === 'activated') { cleanup(); resolve() }
      }
      // Subscribe before checking: ready/claim may occur between awaited operations.
      navigator.serviceWorker.addEventListener('controllerchange', check)
      check()
    })
  }, timeout)
  await expect.poll(() => Boolean(registrationId), { timeout, message: 'CDP observed built-worker registration' }).toBe(true)
  await expect.poll(() => [...versions.values()].some(version => version.registrationId === registrationId && version.status === 'activated' && version.runningStatus === 'running'), { timeout, message: 'CDP built worker activated and running' }).toBe(true)
  assert.ok(registrationId, 'CDP observed built-worker registration')
  assert.equal(await page.evaluate(() => Notification.permission), 'granted', 'harness granted the notification permission')
  // The worker the page just registered is the built artifact, not a stub: fetch it past the
  // worker and require all three U7 listeners in the served bytes.
  const served = await (await page.request.get(testBaseUrl + '/sw.js')).text()
  for (const listener of ['addEventListener("push"', 'addEventListener("notificationclick"', 'addEventListener("pushsubscriptionchange"']) {
    assert.ok(served.includes(listener), 'served sw.js contains ' + listener)
  }
  const payload = { v: 1, kind: 'daily', title: 'Built worker reminder', body: 'Backend body', localDate: '2026-10-20', expiresAt: '2099-10-20T21:30:00Z', streak: 12, skipAvailable: true, url: '/?start=1' }
  let delivered = 0
  const deliver = async value => {
    const start = Date.now()
    const data = JSON.stringify(value)
    console.log('PUSH BROWSER inject:', value.title, JSON.stringify([...versions.values()]))
    await cdp.send('ServiceWorker.deliverPushMessage', { origin: testBaseUrl, registrationId, data })
    delivered++
    await expect.poll(() => dispatchedPushes.length, { timeout, message: 'built worker dispatched synthetic push ' + delivered }).toBe(delivered)
    assert.equal(dispatchedPushes[delivered - 1].eventMetadata.find(metadata => metadata.key === 'Payload')?.value, data, 'worker dispatched the exact CDP payload')
    await expect.poll(() => completedPushes.length, { timeout, message: 'built push handler completed event.waitUntil for push ' + delivered }).toBe(delivered)
    assert.equal(completedPushes[delivered - 1].eventMetadata.find(metadata => metadata.key === 'Status')?.value, 'Success', 'native push event completed successfully')
    console.log('PUSH BROWSER received:', delivered, 'elapsedMs=' + (Date.now() - start))
  }
  const notifications = () => page.evaluate(async () => (await (await navigator.serviceWorker.ready).getNotifications({ tag: 'daily' })).map(notification => ({ title: notification.title, body: notification.body, tag: notification.tag, data: notification.data, silent: notification.silent })))
  const waitForNotification = async title => {
    const start = Date.now()
    await expect.poll(async () => (await notifications()).map(notification => notification.title), { timeout, message: 'real daily notification shown: ' + title }).toContain(title)
    console.log('PUSH BROWSER shown:', title, 'elapsedMs=' + (Date.now() - start))
  }
  await deliver(payload)
  await waitForNotification('Built worker reminder')
  let shown = await notifications()
  assert.equal(shown.length, 1); assert.equal(shown[0].body, 'Backend body')
  assert.equal(shown[0].data.url, testBaseUrl + '/?start=1')
  await deliver({ ...payload, title: 'Replacement reminder' })
  await waitForNotification('Replacement reminder')
  shown = await notifications(); assert.equal(shown.length, 1, 'stable tag replaces rather than stacks')
  await deliver({ ...payload, expiresAt: '2000-01-01T00:00:00Z' })
  await waitForNotification('Repaso pendiente')
  shown = await notifications(); assert.equal(shown.length, 1); assert.equal(shown[0].silent, true)
  await page.evaluate(async () => { for (const notification of await (await navigator.serviceWorker.ready).getNotifications()) notification.close() })
  console.log('PUSH BROWSER: built sw.js, synthetic CDP push delivery: backend title/body displayed, daily tag replaces instead of stacking, expired payload silent; no subscription UI, no real push service.')
  await context.close()
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
