import { chromium } from '@playwright/test'
import { preview } from 'vite'
import { runDayAcceptanceBrowser, runDayBrowser } from './learnBrowser.mjs'
import { testHost, testPort } from './browserConfig.mjs'
import { runDayPanelBrowser } from './dayPanelBrowser.mjs'

const server = await preview({ preview: { host: testHost, port: testPort, strictPort: true } })
let browser
try {
  browser = await chromium.launch({ headless: true })
  await runDayBrowser(browser)
  await runDayAcceptanceBrowser(browser)
  await runDayPanelBrowser(browser)
} finally {
  await browser?.close()
  await new Promise(resolve => server.httpServer.close(resolve))
}
