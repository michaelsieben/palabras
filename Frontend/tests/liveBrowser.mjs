import assert from 'node:assert/strict'
import { chromium } from '@playwright/test'
import { preview } from 'vite'

const backend = process.env.S10_BACKEND_URL ?? 'http://127.0.0.1:5080'
assert.ok(['localhost', '127.0.0.1'].includes(new URL(backend).hostname), 'Live harness requires isolated loopback backend')
const password = process.env.S10_PASSWORD
assert.ok(password?.length >= 12, 'Set throwaway S10_PASSWORD')
const server = await preview({ preview: { host: '127.0.0.1', port: 4174, strictPort: true, proxy: { '/api': { target: backend, changeOrigin: true } } } })
let browser
try {
  browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' })
  try {
    const page = await context.newPage(); const errors = []; const responses = []; const responseJobs = []
    page.on('pageerror', error => errors.push(error.message))
    // No API route mocks: observations come from actual .NET/SQLite attempt responses.
    page.on('response', response => {
      if (new URL(response.url()).pathname === '/api/learn/attempts') {
        responseJobs.push((async () => {
          assert.equal(response.status(), 200)
          const sent = response.request().postDataJSON().attempts
          const body = await response.json()
          responses.push({ sent, results: body.results })
        })())
      }
    })
    async function login(username) {
      await page.getByLabel('Usuario', { exact: true }).fill(username)
      await page.getByLabel('Contraseña', { exact: true }).fill(password)
      await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
    }
    await page.goto('http://127.0.0.1:4174/sesion')
    await login('learner')
    const answer = page.getByLabel('Tu respuesta', { exact: true })
    await answer.fill('el autobus')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByText(/Only the accent differs; counted as correct/).waitFor()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText('Respuestas enviadas.', { exact: true }).waitFor()
    await page.getByText(/Tomorrow: ~\d+ due · this week: ~\d+ due\./).waitFor()
    await Promise.all(responseJobs)
    assert.equal(responses.length, 1)
    assert.deepEqual(responses[0].results.map(item => item.status), ['applied'])
    assert.equal(responses[0].sent[0].isCorrectMeaning, true)
    assert.equal(responses[0].sent[0].isCorrectForm, false)
    assert.equal(responses[0].sent[0].rating, 3)
    console.log('LIVE PASS: real login → typed accent near-miss → summary/forecast; server status applied.')

    await context.setOffline(true)
    await page.getByRole('button', { name: 'Otra vuelta', exact: true }).click()
    await page.getByText(/Repaso sin conexión: última cola guardada/).waitFor()
    await answer.fill('el autobus')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText("Offline: 1 answers saved on this phone, sent when you're back online.", { exact: true }).waitFor()
    const saved = await page.evaluate(() => new Promise((resolve, reject) => {
      const open = indexedDB.open('palabras-attempts:learner', 1)
      open.onerror = () => reject(open.error)
      open.onsuccess = () => {
        const db = open.result; const req = db.transaction('attempts').objectStore('attempts').getAll()
        req.onsuccess = () => { db.close(); resolve(req.result) }; req.onerror = () => { db.close(); reject(req.error) }
      }
    }))
    assert.equal(saved.length, 1); assert.equal(responses.length, 1)
    // Trigger stays alive when session component unmounts.
    await page.getByRole('link', { name: 'Palabras', exact: true }).click()
    const flushResponse = page.waitForResponse(response => new URL(response.url()).pathname === '/api/learn/attempts'
      && response.request().postDataJSON().attempts.some(attempt => attempt.clientAttemptId === saved[0].clientAttemptId))
    await context.setOffline(false)
    const appliedResponse = await flushResponse
    assert.equal(appliedResponse.status(), 200)
    const appliedBody = await appliedResponse.json()
    assert.deepEqual(appliedBody.results.filter(item => item.clientAttemptId === saved[0].clientAttemptId).map(item => item.status), ['applied'])
    await page.waitForFunction(() => new Promise(resolve => {
      const open = indexedDB.open('palabras-attempts:learner', 1)
      open.onsuccess = () => { const db = open.result; const req = db.transaction('attempts').objectStore('attempts').count(); req.onsuccess = () => { db.close(); resolve(req.result === 0) } }
    }))
    await Promise.all(responseJobs)
    const queued = responses.flatMap(batch => batch.results).filter(item => item.clientAttemptId === saved[0].clientAttemptId)
    assert.deepEqual(queued.map(item => item.status), ['applied'])
    const audit = await page.evaluate(async attempt => {
      const response = await fetch('/api/learn/attempts', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json', 'X-Palabras': '1' }, body: JSON.stringify({ attempts: [attempt] }) })
      return { status: response.status, body: await response.json() }
    }, saved[0])
    assert.equal(audit.status, 200)
    assert.deepEqual(audit.body.results.map(item => item.status), ['duplicate'])
    // Complete online-trigger cycles, then recheck actual response statuses for this UUID.
    await page.evaluate(() => { window.dispatchEvent(new Event('online')); window.dispatchEvent(new Event('online')) })
    await page.reload()
    await page.getByRole('navigation', { name: 'Navegación alumno' }).waitFor()
    await Promise.all(responseJobs)
    const final = responses.flatMap(batch => batch.results).filter(item => item.clientAttemptId === saved[0].clientAttemptId)
    assert.deepEqual(final.map(item => item.status), ['applied', 'duplicate'])
    console.log(`LIVE PASS: setOffline(true) cached-plan session → reconnect; UUID ${saved[0].clientAttemptId} applied exactly once; deliberate same-UUID audit returned duplicate; racing online events/reload added no application.`)

    await page.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
    await login('reviewer')
    await page.getByRole('navigation', { name: 'Navegación revisora' }).waitFor()
    await page.goto('http://127.0.0.1:4174/sesion')
    await page.getByRole('navigation', { name: 'Navegación revisora' }).waitFor()
    assert.equal(new URL(page.url()).pathname, '/revisar')
    assert.equal(await answer.count(), 0)
    const denied = await page.evaluate(async () => { const response = await fetch('/api/learn/session/plan'); return { status: response.status, body: await response.json() } })
    assert.equal(denied.status, 403); assert.equal(denied.body.error, 'forbidden')
    assert.deepEqual(errors, [])
    console.log('LIVE PASS: real reviewer login cannot open /sesion; real learner plan endpoint returns 403 forbidden.')
    console.log('Live M6 verification complete: 3/3 scenarios pass; actual backend statuses checked, no API mocks.')
  } finally { await context.close() }
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
