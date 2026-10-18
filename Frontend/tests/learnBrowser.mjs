import assert from 'node:assert/strict'

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
    let page = await context.newPage()
    page.on('pageerror', error => errors.push(error.message))
    await page.goto('http://127.0.0.1:4173/sesion')
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
    await page.goto('http://127.0.0.1:4173/sesion')
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
    await page.goto('http://127.0.0.1:4173/sesion')
    await page.getByRole('navigation', { name: 'Navegación revisora' }).waitFor()
    assert.equal(new URL(page.url()).pathname, '/revisar')
    assert.equal(await answer.count(), 0); assert.equal(planRequests, before)
    assert.deepEqual(errors, [])
    console.log('M6 browser: login → typed accent near-miss → summary/forecast; setOffline airplane-mode cached-plan session → durable IndexedDB row → reconnect applied once; racing online events/reload no resend; close offline/reopen online drains persisted row once; reviewer /sesion denied. API contract mocked, no live backend.')
  } finally { await context.close() }
}
