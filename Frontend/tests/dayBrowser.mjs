import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { runDayAcceptanceBrowser, runDayBrowser } from './learnBrowser.mjs'

const server = await preview({ preview: { host: '127.0.0.1', port: 4173, strictPort: true } })
let browser
try {
  browser = await chromium.launch({ headless: true })
  await runDayBrowser(browser)
  await runDayAcceptanceBrowser(browser)
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
