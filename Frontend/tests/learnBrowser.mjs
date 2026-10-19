import assert from 'node:assert/strict'
import { testBaseUrl } from './browserConfig.mjs'
import { reviewBrowserFixture } from './reviewBrowserFixture.mjs'

export const browserPlan = (typed = false) => ({
  sessionId: 'b0cd98f1-dd5c-49ab-9ced-987865dd2100', generatedAtUtc: '2026-10-18T12:00:00Z', localDate: '2026-10-18',
  counts: { due: typed ? 1 : 2, newOffered: 0, backlog: 0 }, warnings: [],
  items: Array.from({ length: typed ? 1 : 2 }, (_, index) => ({ cardId: index + 1, cardEpoch: 2,
    direction: typed ? 'en_es' : 'es_en', mode: typed ? 'typed' : 'recognition', isNew: false,
    word: { key: 'es-cafe-01', headword: 'café', article: null, gender: 'masculine', pos: 'noun' },
    sense: { gloss: 'coffee', exampleEs: 'Un café.', exampleEn: 'A coffee.', acceptedEs: ['café'], acceptedEn: ['coffee'], accentsAreThePoint: false }, notes: {},
  })),
})

export async function fulfillLearn(route, typed = false) {
  const path = new URL(route.request().url()).pathname
  if (path.endsWith('/plan')) return route.fulfill({ json: browserPlan(typed) })
  if (path.endsWith('/words')) return route.fulfill({ json: { items: [] } })
  if (path.endsWith('/forecast')) return route.fulfill({ json: { tomorrow: 2,
    next7Days: Array.from({ length: 7 }, (_, i) => ({ date: `2026-10-${19 + i}`, due: i === 0 ? 2 : i === 1 ? 7 : 0 })) } })
  if (path.includes('/day/')) return route.fulfill({ json: {
    localDate: path.endsWith('/done') ? route.request().postDataJSON().localDate : '2026-10-18',
    status: path.endsWith('/done') ? 'done' : path.endsWith('/skip') ? 'skipped' : null,
    streak: path.endsWith('/done') ? 1 : 0, skipAvailable: !path.endsWith('/skip'), skipNextAvailable: path.endsWith('/skip') ? '2026-10-25' : null,
  } })
  const { attempts } = route.request().postDataJSON()
  return route.fulfill({ json: { results: attempts.map(a => ({ clientAttemptId: a.clientAttemptId, status: 'applied' })) } })
}

export async function runLearnerBrowser(browser) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' })
  try {
    let user = null; let offline = false; let planRequests = 0
    const batches = []; const applied = new Set(); const errors = []
    await context.route('**/api/**', async route => {
      const request = route.request(); const path = new URL(request.url()).pathname
      if (offline) return route.abort('internetdisconnected')
      if (request.method() !== 'GET') assert.equal(request.headers()['x-palabras'], '1')
      if (path.endsWith('/me')) return route.fulfill({ status: user ? 200 : 401, json: user ?? { error: 'unauthorized' } })
      if (path.endsWith('/login')) {
        const credentials = request.postDataJSON()
        assert.equal(credentials.password, 'correct')
        user = { username: credentials.username, role: credentials.username === 'reviewer' ? 'reviewer' : 'owner' }
        return route.fulfill({ json: user })
      }
      if (path.endsWith('/logout')) { user = null; return route.fulfill({ status: 204 }) }
      if (path.endsWith('/plan')) planRequests++
      if (path.endsWith('/attempts')) {
        const { attempts } = request.postDataJSON(); batches.push(attempts)
        assert.ok(attempts.length >= 1 && attempts.length <= 50)
        // Reject duplicate sends before simulating server dedupe: client must stand on its own.
        for (const a of attempts) { assert.ok(!applied.has(a.clientAttemptId), 'client sent UUID twice'); applied.add(a.clientAttemptId) }
      }
      return fulfillLearn(route, true)
    })
    // Reviewer login below mounts the review UI, which fetches GET /api/review/queue. Answer it explicitly instead of
    // falling through to fulfillLearn's attempts-POST shape. Playwright matches the most recently registered route
    // first, so this must stay after the catch-all.
    const review = reviewBrowserFixture()
    await context.route('**/api/review/**', route => offline ? route.abort('internetdisconnected') : review.fulfill(route))
    let page = await context.newPage()
    page.on('pageerror', error => errors.push(error.message))
    await page.goto(`${testBaseUrl}/sesion`)
    await page.getByLabel('Usuario', { exact: true }).fill('learner')
    await page.getByLabel('Contraseña', { exact: true }).fill('correct')
    await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
    let answer = page.getByLabel('Tu respuesta', { exact: true })
    await answer.fill('cafe')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByText(/Only the accent differs; counted as correct/).waitFor()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText('Respuestas enviadas.', { exact: true }).waitFor()
    await page.getByText('Tomorrow: ~2 due · this week: ~9 due.', { exact: true }).waitFor()
    assert.equal(batches.length, 1)
    assert.equal(batches[0][0].isCorrectMeaning, true); assert.equal(batches[0][0].isCorrectForm, false)
    assert.equal(batches[0][0].rating, 3)

    offline = true; await context.setOffline(true)
    await page.getByRole('button', { name: 'Otra vuelta', exact: true }).click()
    await page.getByText(/Repaso sin conexión: última cola guardada/).waitFor()
    await answer.fill('cafe')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText('Offline: 1 answers saved on this phone, sent when you\'re back online.', { exact: true }).waitFor()
    assert.equal(batches.length, 1)
    const saved = await page.evaluate(async () => {
      const db = await new Promise((resolve, reject) => { const req = indexedDB.open('palabras-attempts:learner', 1); req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error) })
      try { return await new Promise((resolve, reject) => { const req = db.transaction('attempts').objectStore('attempts').getAll(); req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error) }) }
      finally { db.close() }
    })
    assert.equal(saved.length, 1); assert.equal(saved[0].cardEpoch, 2)
    assert.equal(saved[0].sessionId, browserPlan(true).sessionId); assert.equal(saved[0].answerText, 'cafe')
    assert.ok(saved[0].durationMs >= 0); assert.ok(saved[0].reviewedAtUtc)
    // Unmount learner route: online trigger must live at authenticated app boundary.
    await page.getByRole('link', { name: 'Palabras', exact: true }).click()
    offline = false; await context.setOffline(false)
    await page.getByRole('link', { name: 'Hoy', exact: true }).click()
    await answer.waitFor()
    await page.waitForFunction(() => new Promise(resolve => {
      const open = indexedDB.open('palabras-attempts:learner', 1)
      open.onsuccess = () => { const db = open.result; const req = db.transaction('attempts').objectStore('attempts').count(); req.onsuccess = () => { db.close(); resolve(req.result === 0) } }
    }))
    assert.equal(batches.length, 2); assert.equal(batches[1][0].clientAttemptId, saved[0].clientAttemptId)
    await page.evaluate(() => { window.dispatchEvent(new Event('online')); window.dispatchEvent(new Event('online')) })
    await page.reload(); await answer.waitFor()
    assert.equal(batches.length, 2); assert.equal(applied.size, 2)
    assert.ok(planRequests >= 2)

    // Reload durability without weakening auth: close offline, reopen once /me can confirm identity.
    offline = true; await context.setOffline(true)
    await answer.fill('cafe')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText('Offline: 1 answers saved on this phone, sent when you\'re back online.', { exact: true }).waitFor()
    assert.equal(batches.length, 2)
    await page.close()
    offline = false; await context.setOffline(false)
    page = await context.newPage()
    page.on('pageerror', error => errors.push(error.message))
    answer = page.getByLabel('Tu respuesta', { exact: true })
    await page.goto(`${testBaseUrl}/sesion`)
    await answer.waitFor()
    await page.waitForFunction(() => new Promise(resolve => {
      const open = indexedDB.open('palabras-attempts:learner', 1)
      open.onsuccess = () => { const db = open.result; const req = db.transaction('attempts').objectStore('attempts').count(); req.onsuccess = () => { db.close(); resolve(req.result === 0) } }
    }))
    assert.equal(batches.length, 3); assert.equal(applied.size, 3)
    assert.notEqual(batches[2][0].clientAttemptId, saved[0].clientAttemptId)
    await page.getByRole('button', { name: 'Cerrar sesión', exact: true }).click()
    await page.getByLabel('Usuario', { exact: true }).fill('reviewer')
    await page.getByLabel('Contraseña', { exact: true }).fill('correct')
    await page.getByRole('button', { name: 'Iniciar sesión', exact: true }).click()
    await page.getByRole('button', { name: 'No ✗', exact: true }).first().waitFor()
    const before = planRequests
    await page.goto(`${testBaseUrl}/sesion`)
    await page.getByRole('navigation', { name: 'Navegación revisora' }).waitFor()
    assert.equal(new URL(page.url()).pathname, '/revisar')
    assert.equal(await answer.count(), 0); assert.equal(planRequests, before)
    assert.deepEqual(errors, [])
    console.log('M6 browser: login → typed accent near-miss → summary/forecast; setOffline airplane-mode cached-plan session → durable IndexedDB row → reconnect applied once; racing online events/reload no resend; close offline/reopen online drains persisted row once; reviewer /sesion denied. API contract mocked, no live backend.')
  } finally { await context.close() }
}

export async function runDayBrowser(browser) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: 'block' })
  try {
    let offline = false; let skipConflict = true
    let day = { localDate: '2026-10-18', status: null, streak: 3, skipAvailable: true, skipNextAvailable: null }
    const events = []; const completions = []; const errors = []
    await context.route('**/api/**', async route => {
      const request = route.request(); const path = new URL(request.url()).pathname
      if (offline) return route.abort('internetdisconnected')
      if (path.endsWith('/me')) return route.fulfill({ json: { username: 'm7-learner', role: 'owner' } })
      if (request.method() !== 'GET') assert.equal(request.headers()['x-palabras'], '1')
      if (path.endsWith('/day/status')) return route.fulfill({ json: day })
      if (path.endsWith('/day/skip')) {
        assert.deepEqual(request.postDataJSON(), { localDate: day.localDate, source: 'app' })
        if (skipConflict) {
          day = { ...day, skipAvailable: false, skipNextAvailable: '2026-10-25' }
          return route.fulfill({ status: 409, json: { reason: 'skip_unavailable', nextAvailable: '2026-10-25' } })
        }
        day = { ...day, status: 'skipped', skipAvailable: false, skipNextAvailable: '2026-10-25' }
        return route.fulfill({ json: day })
      }
      if (path.endsWith('/day/done')) {
        events.push('done'); completions.push(request.postDataJSON())
        day = { ...day, status: request.postDataJSON().localDate === day.localDate ? 'done' : null, streak: 4, skipAvailable: true, skipNextAvailable: null }
        return route.fulfill({ json: { ...day, localDate: request.postDataJSON().localDate } })
      }
      if (path.endsWith('/attempts')) events.push('answers')
      return fulfillLearn(route, true)
    })
    let page = await context.newPage()
    await page.clock.install({ time: new Date('2026-10-18T21:58:00Z') })
    page.on('pageerror', error => errors.push(error.message))
    await page.goto(`${testBaseUrl}/`)
    await page.getByText('18 de octubre de 2026 · Racha: 3 días', { exact: true }).waitFor()
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).click()
    await page.getByText(/Ya has usado el salto. Puedes volver a saltar el 25 de octubre de 2026/).waitFor()
    assert.equal(await page.getByRole('button', { name: 'Saltar hoy', exact: true }).isDisabled(), true)
    assert.equal(await page.getByLabel('Tu respuesta', { exact: true }).isVisible(), true)
    // Restore availability then exercise successful skip without preventing practice.
    skipConflict = false; day = { ...day, skipAvailable: true, skipNextAvailable: null }
    await page.reload()
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).click()
    await page.getByText('Hoy saltado', { exact: true }).waitFor()
    offline = true; await context.setOffline(true)
    // Even completion after midnight uses the server plan's day, not browser time.
    await page.clock.setFixedTime(new Date('2026-10-18T22:01:00Z'))
    await page.getByLabel('Tu respuesta', { exact: true }).fill('cafe')
    await page.getByRole('button', { name: 'Comprobar', exact: true }).click()
    await page.getByRole('button', { name: 'Continuar · Good', exact: true }).click()
    await page.getByText(/1 día completado guardado en este dispositivo, pendiente de envío/).waitFor()
    assert.equal(completions.length, 0)
    const rows = await page.evaluate(async () => {
      const db = await new Promise((resolve, reject) => { const req = indexedDB.open('palabras-completions:m7-learner', 1); req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error) })
      try { return await new Promise((resolve, reject) => { const req = db.transaction('completions').objectStore('completions').getAll(); req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error) }) }
      finally { db.close() }
    })
    assert.equal(rows[0].body.localDate, '2026-10-18'); assert.equal(rows[0].state, 'pending')
    await page.close(); offline = false; await context.setOffline(false)
    page = await context.newPage(); page.on('pageerror', error => errors.push(error.message))
    await page.clock.install({ time: new Date('2026-10-18T22:02:00Z') })
    day = { ...day, localDate: '2026-10-19', status: null }
    await page.goto(`${testBaseUrl}/`)
    await page.getByText('19 de octubre de 2026 · Racha: 4 días', { exact: true }).waitFor()
    await page.getByText('Hoy, por completar', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: 'Saltar hoy', exact: true }).isEnabled(), true)
    assert.equal(await page.getByText(/Próximo salto:/).count(), 0) // Refunded token follows the server's today payload.
    assert.deepEqual(events, ['answers', 'done']); assert.equal(completions.length, 1)
    assert.equal(completions[0].localDate, '2026-10-18')
    await page.reload(); await page.getByLabel('Tu respuesta', { exact: true }).waitFor()
    assert.equal(completions.length, 1)
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true)
    assert.deepEqual(errors, [])
    console.log('M7 browser: mobile status/streak; camelCase 409 explains next skip date without blocking practice; successful skip; completion after midnight preserves server plan date; offline completion durably stored; close/reopen drains answers then original-day completion once; today refetched, reload dedupe; no horizontal overflow/page errors. Mock API, user visual confirmation still needed.')
  } finally { await context.close() }
}

export async function runDayAcceptanceBrowser(browser) {
  // Client consumes authoritative statuses; these mocks do not prove server ledger rules.
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, timezoneId: 'America/Los_Angeles', serviceWorkers: 'block' })
  try {
    let day = { localDate: '2026-10-25', status: 'missed', streak: 0, skipAvailable: true, skipNextAvailable: null }
    let skipResponse = 'success'; let skips = 0; let attempts = 0; let completions = 0
    let delayRead = false; let releaseRead; let readStarted
    await context.route('**/api/**', async route => {
      const request = route.request(); const path = new URL(request.url()).pathname
      if (path.endsWith('/me')) return route.fulfill({ json: { username: 'm7-acceptance', role: 'owner' } })
      if (path.endsWith('/day/status')) {
        const snapshot = { ...day }
        if (delayRead) {
          delayRead = false
          await new Promise(resolve => { releaseRead = resolve; readStarted() })
        }
        return route.fulfill({ json: snapshot })
      }
      if (path.endsWith('/day/skip')) {
        skips++
        assert.deepEqual(request.postDataJSON(), { localDate: day.localDate, source: 'app' })
        assert.equal(request.headers()['x-palabras'], '1')
        if (skipResponse === 'already_done') {
          day = { ...day, status: 'done', streak: 6 }
          return route.fulfill({ status: 409, json: { reason: 'already_done' } })
        }
        if (skipResponse === 'stale') {
          day = { ...day, localDate: '2026-10-26' }
          return route.fulfill({ status: 400, json: { error: 'bad_request' } })
        }
        day = { ...day, status: 'skipped', skipAvailable: false, skipNextAvailable: '2026-11-01' }
        return route.fulfill({ json: day })
      }
      if (path.endsWith('/attempts')) attempts++
      if (path.endsWith('/day/done')) completions++
      return fulfillLearn(route, true)
    })
    const page = await context.newPage()
    const errors = []; page.on('pageerror', error => errors.push(error.message))
    await page.clock.install({ time: new Date('2026-10-25T12:00:00Z') })
    await page.goto(`${testBaseUrl}/`)
    await page.getByText('Hoy puedes empezar otra racha', { exact: true }).waitFor()
    await page.getByText('25 de octubre de 2026 · Racha: 0 días', { exact: true }).waitFor()
    assert.equal(skips, 0); assert.equal(attempts, 0); assert.equal(completions, 0)

    // Day N+7 in a DST week is available only because the server says so.
    day = { ...day, status: null, streak: 5 }
    await page.evaluate(() => window.dispatchEvent(new Event('focus')))
    await page.getByText('25 de octubre de 2026 · Racha: 5 días', { exact: true }).waitFor()
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).click()
    await page.getByText('Hoy saltado', { exact: true }).waitFor()
    await page.getByText('Próximo salto: 1 de noviembre de 2026.', { exact: true }).waitFor()
    assert.equal(skips, 1); assert.equal(attempts, 0); assert.equal(completions, 0)
    assert.equal(await page.getByText('25 de octubre de 2026 · Racha: 5 días', { exact: true }).count(), 1)
    assert.equal(await page.getByLabel('Tu respuesta', { exact: true }).isVisible(), true)

    day = { ...day, status: null, skipAvailable: true, skipNextAvailable: null }; skipResponse = 'already_done'
    await page.reload()
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).click()
    await page.getByText('Ya has completado hoy. Puedes seguir practicando.', { exact: true }).waitFor()
    await page.getByText('Día completado', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: 'Saltar hoy', exact: true }).count(), 0)

    day = { ...day, status: null }; skipResponse = 'stale'
    await page.reload()
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).click()
    await page.getByText('Ha cambiado el día. Actualizamos la fecha; vuelve a intentarlo.', { exact: true }).waitFor()
    await page.getByText('26 de octubre de 2026 · Racha: 6 días', { exact: true }).waitFor()
    assert.equal(skips, 3) // A stale tap never retries automatically for the new day.
    await context.setOffline(true)
    await page.getByText(/Sin conexión: día y racha sin actualizar/).waitFor()
    assert.equal(await page.getByRole('button', { name: 'Saltar hoy', exact: true }).isDisabled(), true)
    await context.setOffline(false)
    await page.getByRole('button', { name: 'Saltar hoy', exact: true }).waitFor({ state: 'visible' })

    // Periodic refresh notices midnight without a navigation/focus event.
    day = { ...day, localDate: '2026-10-27' }
    await page.clock.runFor(60_000)
    await page.getByText('27 de octubre de 2026 · Racha: 6 días', { exact: true }).waitFor()

    // A slow old response cannot replace a newer server date/status.
    const started = new Promise(resolve => { readStarted = resolve })
    delayRead = true
    await page.evaluate(() => window.dispatchEvent(new Event('focus')))
    await started
    day = { ...day, localDate: '2026-10-28', status: 'done', streak: 7 }
    await page.evaluate(() => window.dispatchEvent(new Event('focus')))
    await page.getByText('28 de octubre de 2026 · Racha: 7 días', { exact: true }).waitFor()
    const oldResponse = page.waitForResponse(response => response.url().endsWith('/day/status'))
    releaseRead(); await oldResponse
    // Next animation frames allow the old fetch/React state update to settle.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    assert.equal(await page.getByText('28 de octubre de 2026 · Racha: 7 días', { exact: true }).count(), 1)
    assert.deepEqual(errors, [])
    console.log('M7 delta-D client: missed/zero streak consumes no skip; server N+7/DST availability, skip preserves streak and writes no attempts/done; already_done conflict; stale-date rejection refresh without auto-spend; offline skip disabled; visible-minute day refresh; old response cannot overwrite newer date. Zone-setting endpoint remains absent; no claim of server-rule/device verification.')
  } finally { await context.close() }
}
