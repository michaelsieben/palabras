import assert from 'node:assert/strict'
import { chromium, expect } from '@playwright/test'
import { preview } from 'vite'
import { testBaseUrl, testHost, testPort } from './browserConfig.mjs'
import { reviewBrowserFixture } from './reviewBrowserFixture.mjs'
import { reviewWord, approvedWord } from './reviewFixtures.mjs'

const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
let browser
async function session(fixture, viewport = { width: 390, height: 844 }) {
  const context = await browser.newContext({ serviceWorkers: 'block', viewport, reducedMotion: 'reduce' })
  let username = 'reviewer-a'
  await context.route('**/api/auth/me', route => route.fulfill({ json: { username, role: 'reviewer' } }))
  await context.route('**/api/review/**', route => fixture.fulfill(route))
  const page = await context.newPage(); const errors = []
  page.on('pageerror', e => errors.push(e.message))
  return { context, page, errors, setUsername: u => { username = u } }
}
try {
  browser = await chromium.launch({ headless: true })
  // Real component + IndexedDB: authenticated offline chip, durable reload, account isolation.
  {
    const fixture = reviewBrowserFixture({ pending: [reviewWord(1), reviewWord(2)] })
    const { context, page, errors, setUsername } = await session(fixture)
    try {
      await page.goto(`${testBaseUrl}/revisar`)
      await page.getByRole('button', { name: 'No ✗', exact: true }).first().click()
      await expect(page.getByRole('button', { name: 'Cerrar alternativas' })).toBeFocused()
      await page.getByRole('button', { name: 'Variante regional (solo lectura)', exact: true }).click()
      await expect(page.locator('.readonly-variant')).toContainText('Solo lectura')
      assert.equal(await page.locator('.readonly-variant button').count(), 0)
      await page.getByRole('button', { name: 'Variante de registro (solo lectura)', exact: true }).click()
      await expect(page.locator('.readonly-variant')).toContainText('variante formal')
      await page.getByRole('button', { name: 'Cerrar alternativas' }).click()
      await expect(page.getByRole('button', { name: 'No ✗', exact: true }).first()).toBeFocused()
      await context.setOffline(true)
      await page.getByRole('button', { name: 'No ✗', exact: true }).first().click()
      await page.getByRole('button', { name: 'Palabra', exact: true }).click()
      await page.locator('.alternative-list button').click()
      const outbox = page.getByRole('region', { name: 'Decisiones de este dispositivo' })
      await expect(outbox).toContainText('pendiente de envío')
      assert.equal(fixture.sent.length, 0)
      const stored = await page.evaluate(async () => {
        const db = await new Promise((resolve, reject) => { const r = indexedDB.open('palabras-review:reviewer-a'); r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error) })
        const rows = await new Promise(resolve => { const r = db.transaction('actions').objectStore('actions').getAll(); r.onsuccess = () => resolve(r.result) }); db.close(); return rows
      })
      assert.equal(stored[0].action.action, 'approve_alt'); assert.equal(stored[0].word.headword, 'palabra-1')
      fixture.failRequests = true; fixture.failQueue = true
      await context.setOffline(false)
      await expect(outbox).toContainText('Envío sin confirmar')
      await page.reload()
      await expect(page.getByText(/Sin conexión: cola guardada/)).toBeVisible()
      await expect(page.getByRole('region', { name: 'Decisiones de este dispositivo' })).toContainText('palabra-1')
      setUsername('reviewer-b'); await page.reload()
      await expect(page.getByText(/No se ha podido cargar la cola/)).toBeVisible()
      assert.equal(await page.getByRole('region', { name: 'Decisiones de este dispositivo' }).count(), 0)
      setUsername('reviewer-a'); await page.reload()
      fixture.failRequests = false; fixture.failQueue = false
      await page.getByRole('button', { name: 'Reintentar envío' }).click()
      await expect(page.getByRole('region', { name: 'Decisiones de este dispositivo' })).toContainText('Decisión confirmada.')
      assert.equal(fixture.results.size, 1)
      assert.ok(fixture.sent.every(batch => batch[0].clientReviewId === stored[0].action.clientReviewId))
      assert.deepEqual(errors, [])
    } finally { await context.close() }
  }
  // Sitting5/30 persistence, strict eligibility, batch-approve10 and checklist25.
  {
    const fixture = reviewBrowserFixture({ pending: Array.from({ length: 30 }, (_, i) => reviewWord(i + 1, { band: 'alta', agreement: 0.95 })) })
    const { context, page, errors } = await session(fixture, { width: 1600, height: 1000 })
    try {
      await page.goto(`${testBaseUrl}/revisar`)
      const size = page.getByLabel('Palabras por sesión')
      await expect(size).toHaveValue('15'); await size.selectOption('5')
      await expect(page.getByRole('button', { name: 'Vale ✓', exact: true })).toHaveCount(5)
      await page.reload(); await expect(size).toHaveValue('5')
      await size.selectOption('30')
      const checks = page.getByRole('region', { name: 'Aprobación por lote' }).getByRole('checkbox')
      await expect(checks).toHaveCount(30)
      for (let i = 0; i < 25; i++) await checks.nth(i).check()
      await expect(checks.nth(25)).toBeDisabled()
      for (let i = 10; i < 25; i++) await checks.nth(i).uncheck()
      await page.getByRole('button', { name: 'Aprobar seleccionadas (10)', exact: true }).click()
      await expect(page.getByRole('region', { name: 'Decisiones de este dispositivo' }).getByText('Decisión confirmada.', { exact: true })).toHaveCount(10)
      assert.equal(fixture.sent.length, 1); assert.equal(fixture.sent[0].length, 10); assert.ok(fixture.sent[0].every(a => a.viaBatch && a.wordVersion === 3))
      assert.equal(new Set(fixture.sent[0].map(a => a.clientReviewId)).size, 10)
      const layout = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, width: innerWidth, columns: getComputedStyle(document.querySelector('.review-grid')).gridTemplateColumns.split(' ').length }))
      assert.ok(layout.scroll <= layout.width); assert.equal(layout.columns, 3)
      assert.deepEqual(errors, [])
    } finally { await context.close() }
  }
  // Row failures distinct/actionable; stale edit re-read, reject/needs_change and no no-op edits.
  {
    const fixture = reviewBrowserFixture({ pending: [reviewWord(1), reviewWord(2), reviewWord(3), reviewWord(4)] })
    fixture.outcome = a => a.wordId === 1 ? 'stale_version' : a.wordId === 2 ? 'invalid_transition' : a.wordId === 3 ? 'invalid' : 'applied'
    const { context, page, errors } = await session(fixture)
    try {
      await page.goto(`${testBaseUrl}/revisar`)
      for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'Vale ✓', exact: true }).first().click()
      const outbox = page.getByRole('region', { name: 'Decisiones de este dispositivo' })
      await expect(outbox).toContainText('La palabra ha cambiado'); await expect(outbox).toContainText('Ya no está en un estado revisable'); await expect(outbox).toContainText('No se pudo aplicar esta decisión')
      await expect(outbox.getByRole('button', { name: 'Reintentar envío' })).toHaveCount(3)
      const stale = outbox.getByRole('article').filter({ hasText: 'palabra-1' })
      fixture.pending[0].version = 4
      await stale.getByRole('button', { name: 'Actualizar palabra' }).click()
      const card = page.getByRole('article', { name: 'la palabra-1', exact: true })
      await card.getByRole('button', { name: 'Editar ✎' }).click()
      await expect(card.getByRole('button', { name: 'Guardar y aprobar ✓' })).toBeDisabled()
      await card.locator('input[lang="en"]').fill('edited gloss')
      fixture.outcome = undefined
      await card.getByRole('button', { name: 'Guardar y aprobar ✓' }).click()
      await expect(outbox).toContainText('Decisión confirmada')
      const edit = fixture.sent.flat().find(a => a.action === 'approve_edit')
      assert.equal(edit.wordVersion, 4); assert.deepEqual(edit.edits, [{ field: 'gloss', senseOrder: 0, after: 'edited gloss' }])
      await page.getByRole('button', { name: 'No ✗', exact: true }).first().click()
      await page.getByRole('button', { name: 'Ninguna vale… Cambiar ⚑' }).click()
      await page.getByLabel('Nota (opcional)').fill('Reescribir')
      await page.getByRole('button', { name: 'Marcar para cambiar ⚑', exact: true }).click()
      await expect(outbox).toContainText('needs_change')
      assert.deepEqual(errors, [])
    } finally { await context.close() }
  }
  // Manual corpus has no batch control. Five decisions end sitting; another sitting starts explicitly.
  {
    const fixture = reviewBrowserFixture({ pending: Array.from({ length: 8 }, (_, i) => reviewWord(i + 1)) })
    const { context, page, errors } = await session(fixture)
    try {
      await page.goto(`${testBaseUrl}/revisar`)
      await expect(page.getByRole('button', { name: 'Vale ✓', exact: true })).toHaveCount(8)
      assert.equal(await page.getByRole('region', { name: 'Aprobación por lote' }).count(), 0)
      await page.getByLabel('Palabras por sesión').selectOption('5')
      await page.getByRole('button', { name: 'No ✗', exact: true }).first().click()
      await page.getByRole('button', { name: 'Rechazar palabra', exact: true }).click()
      for (let i = 0; i < 4; i++) await page.getByRole('button', { name: 'Vale ✓', exact: true }).first().click()
      await expect(page.getByRole('button', { name: 'Vale ✓', exact: true })).toHaveCount(0)
      await expect(page.getByRole('button', { name: '¿Otras 5?', exact: true })).toBeVisible()
      await page.getByRole('button', { name: 'Listo por hoy', exact: true }).click()
      await page.getByRole('button', { name: 'Continuar revisión', exact: true }).click()
      await expect(page.getByRole('button', { name: 'Vale ✓', exact: true })).toHaveCount(3)
      assert.equal(fixture.sent.flat().filter(a => a.action === 'reject').length, 1)
      assert.deepEqual(errors, [])
    } finally { await context.close() }
  }
  // Approved search/page2 stale cursor reset and version7 demote; failed demote stays per-row.
  {
    const fixture = reviewBrowserFixture()
    const { context, page, errors } = await session(fixture)
    try {
      await page.goto(`${testBaseUrl}/aprobadas`)
      await expect(page.getByRole('article')).toHaveCount(30)
      fixture.staleCursor = true
      await page.getByRole('button', { name: 'Siguiente', exact: true }).click()
      await expect(page.getByText(/Volvemos a la página 1/)).toBeVisible()
      await expect(page.getByRole('article')).toHaveCount(30)
      await page.getByRole('button', { name: 'Siguiente', exact: true }).click()
      await expect(page.getByRole('article')).toHaveCount(5)
      await page.getByLabel('Buscar aprobadas').fill('aprobada-101')
      await page.getByRole('button', { name: 'Buscar', exact: true }).click()
      await expect(page.getByRole('article')).toHaveCount(1)
      fixture.outcome = () => 'stale_version'
      await page.getByRole('button', { name: 'Revisar otra vez', exact: true }).click()
      await expect(page.getByRole('article')).toContainText('La palabra ha cambiado')
      assert.equal(fixture.sent.at(-1)[0].wordVersion, 7)
      fixture.approved[0] = approvedWord(101, { version: 8 }); fixture.outcome = undefined
      await page.getByRole('button', { name: 'Actualizar palabra', exact: true }).click()
      await page.getByRole('button', { name: 'Revisar otra vez', exact: true }).click()
      await expect(page.getByRole('article')).toContainText('Decisión confirmada')
      assert.equal(fixture.sent.at(-1)[0].wordVersion, 8)
      assert.deepEqual(errors, [])
    } finally { await context.close() }
  }
  console.log('M4 reviewer browser: API fixtures with real App/IndexedDB; offline chip/reload/account scope, read-only variants, sitting5/30, checklist25, batch10, row failures/retry/edit/needs_change, search/pagination/stale cursor and demote current version.')
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
