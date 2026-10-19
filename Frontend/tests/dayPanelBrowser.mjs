import assert from 'node:assert/strict'
import { createServer } from 'vite'

export async function runDayPanelBrowser(browser) {
  // Real component, synthetic external-store snapshots; fixture never enters production build.
  const server = await createServer({ server: { host: '127.0.0.1', port: 0, strictPort: true } })
  const context = await browser.newContext({ serviceWorkers: 'block' })
  try {
    await server.listen()
    const page = await context.newPage()
    const errors = []; page.on('pageerror', error => errors.push(error.message))
    let day = { localDate: '2026-10-25', status: null, streak: 0, skipAvailable: true, skipNextAvailable: null }
    let reads = 0; let gate; let release; let started
    await context.route('**/api/learn/day/status', async route => {
      reads++
      const snapshot = { ...day }
      if (gate) { const pending = gate; gate = undefined; started(); await pending }
      await route.fulfill({ json: snapshot })
    })
    await page.clock.install({ time: new Date('2026-10-25T12:00:00Z') })
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/tests/dayPanelHarness.html`)
    const panel = page.getByRole('region', { name: 'Día y racha' })
    const status = panel.locator('.day-status-heading [role="status"]')
    await status.getByText('Hoy, por completar', { exact: true }).waitFor()
    assert.equal(await status.getAttribute('aria-atomic'), 'true')
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    await settle()
    assert.equal(reads, 1)
    await status.evaluate(el => {
      window.dayStatusMutations = 0
      new MutationObserver(records => { window.dayStatusMutations += records.length }).observe(el, { subtree: true, childList: true, characterData: true })
    })

    // Hold minute fetch open: no transient loading announcement or disabled skip.
    const readStarted = new Promise(resolve => { started = resolve })
    gate = new Promise(resolve => { release = resolve })
    await page.clock.runFor(60_000); await readStarted
    assert.equal(reads, 2)
    assert.equal(await panel.getByRole('button', { name: 'Saltar hoy', exact: true }).isEnabled(), true)
    assert.equal(await panel.getByText('Actualizando día y racha…', { exact: true }).count(), 0)
    const response = page.waitForResponse(res => res.url().endsWith('/day/status'))
    release(); await response; await settle()
    assert.equal(await page.evaluate(() => window.dayStatusMutations), 0, 'unchanged refresh must not mutate live status')

    // A genuine date/status/streak change still updates persistent, atomic live region.
    day = { ...day, localDate: '2026-10-26', status: 'done', streak: 1 }
    await page.clock.runFor(60_000)
    await status.getByText('Día completado', { exact: true }).waitFor()
    assert.match(await status.textContent(), /26 de octubre de 2026 · Racha: 1 día/)
    assert.ok(await page.evaluate(() => window.dayStatusMutations) > 0, 'changed status must update live region')
    assert.equal(reads, 3)

    // New completion payload refetches; cloning it with unrelated queue changes does not.
    let nextResponse = page.waitForResponse(res => res.url().endsWith('/day/status'))
    await page.getByRole('button', { name: 'Publish completion', exact: true }).click()
    await nextResponse; await settle(); assert.equal(reads, 4)
    for (const name of ['Publish completion', 'Enqueue unchanged completion', 'Reject unchanged completion']) {
      await page.getByRole('button', { name, exact: true }).click(); await settle()
      assert.equal(reads, 4, `${name} must not refetch identical completion data`)
    }
    nextResponse = page.waitForResponse(res => res.url().endsWith('/day/status'))
    await page.getByRole('button', { name: 'Publish changed completion', exact: true }).click()
    await nextResponse; await settle(); assert.equal(reads, 5)
    assert.deepEqual(errors, [])
    console.log('M7 panel regression: delayed minute refresh keeps skip enabled, no loading announcement; unchanged live-region DOM untouched; changed day/status/streak announced via atomic status; equivalent completion clones/enqueue/rejection do not refetch; changed payload refetches once. Real component, mocked API/store.')
  } finally { await context.close(); await server.close() }
}
