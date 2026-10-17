import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'
import { generateWorker } from './scripts/pwa.mjs'
import type { Plugin } from 'vite'

// Vite app serves these headers in dev/preview. Production ASP.NET host must do same.
const pwaShell = (): Plugin => {
  let building = false
  return ({
  name: 'palabras-pwa-shell',
  configResolved(config) { building = config.command === 'build' },
  configureServer(server) {
    server.middlewares.use((req, res, next) => {
      if (['/sw.js', '/manifest.webmanifest'].includes((req.url ?? '').split('?')[0])) res.setHeader('Cache-Control', 'no-cache')
      next()
    })
  },
  configurePreviewServer(server) {
    server.middlewares.use((req, res, next) => {
      if (['/sw.js', '/manifest.webmanifest'].includes((req.url ?? '').split('?')[0])) res.setHeader('Cache-Control', 'no-cache')
      next()
    })
  },
  async closeBundle() { if (building) await generateWorker(resolve(import.meta.dirname, 'dist')) },
  })
}

export default defineConfig({
  plugins: [react(), pwaShell()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5080',
      '/healthz': 'http://localhost:5080',
    },
  },
})
