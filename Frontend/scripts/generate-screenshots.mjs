// Deliberate maintainer operation, not run by tests/build. Commit refreshed install screenshots.
import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
const server = await preview({ preview: { host: '127.0.0.1', port: 4173, strictPort: true } })
let browser
try {
  await mkdir(new URL('../public/screenshots/', import.meta.url), { recursive: true })
  browser = await chromium.launch()
  for (const [name, width, height] of [['narrow', 390, 844], ['wide', 2000, 1200]]) {
    const page = await browser.newPage({ viewport: { width, height }, colorScheme: 'light', serviceWorkers: 'block', reducedMotion: 'reduce' })
    await page.goto('http://127.0.0.1:4173/')
    await page.getByRole('button', { name: 'Mostrar respuesta' }).waitFor()
    await page.screenshot({ path: fileURLToPath(new URL(`../public/screenshots/${name}.png`, import.meta.url)), animations: 'disabled' })
    console.log(`Generated manifest screenshot: ${name} ${width}×${height}`)
    await page.close()
  }
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
