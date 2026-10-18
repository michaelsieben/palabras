import { useEffect, useState } from 'react'

type InstallPrompt = Event & { prompt(): Promise<void>; userChoice: Promise<{ outcome: string }> }
export function usePwa() {
  const [state, setState] = useState('Comprobando…')
  const [installPrompt, setInstallPrompt] = useState<InstallPrompt>()
  const [standalone, setStandalone] = useState(() => matchMedia('(display-mode: standalone)').matches)
  useEffect(() => {
    let active = true
    const media = matchMedia('(display-mode: standalone)')
    const displayChanged = () => setStandalone(media.matches)
    const prompt = (event: Event) => { event.preventDefault(); setInstallPrompt(event as InstallPrompt) }
    const installed = () => { setStandalone(true); setInstallPrompt(undefined) }
    window.addEventListener('beforeinstallprompt', prompt)
    window.addEventListener('appinstalled', installed)
    media.addEventListener('change', displayChanged)
    let checking = false
    let controller = navigator.serviceWorker?.controller
    let reloading = false
    const watchedRegistrations = new WeakSet<ServiceWorkerRegistration>()
    const watchedWorkers = new WeakSet<ServiceWorker>()
    const watchWorker = (worker: ServiceWorker | null) => {
      if (!worker || watchedWorkers.has(worker)) return
      watchedWorkers.add(worker)
      worker.addEventListener('statechange', () => {
        if (!active) return
        if (worker.state === 'installed') {
          setState('Página sin conexión disponible')
          worker.postMessage({ type: 'SKIP_WAITING' })
        }
        if (worker.state === 'redundant') setState('Página sin conexión no disponible; la app sigue funcionando')
      })
    }
    const controllerChanged = () => {
      const next = navigator.serviceWorker.controller
      // First installation may claim this document without replacing its worker.
      // Replacement (including migration from legacy /sw.js) reloads every app tab.
      if (controller && next !== controller && !reloading) {
        reloading = true
        window.location.reload()
      }
      controller = next
    }
    const check = async () => {
      if (!active || checking || reloading || document.visibilityState !== 'visible') return
      checking = true
      try {
        // A unique discovery URL defeats a stale plain /sw.js in the proxy.
        // Source digest gives a stable registration URL until actual bytes change.
        const response = await fetch(`/sw.js?check=${crypto.randomUUID()}`, { cache: 'no-store', credentials: 'same-origin' })
        if (!response.ok) throw new Error('Worker unavailable')
        const bytes = await response.arrayBuffer()
        const digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(byte => byte.toString(16).padStart(2, '0')).join('')
        const reg = await navigator.serviceWorker.register(`/sw.js?version=${digest}`, { scope: '/', updateViaCache: 'none' })
        if (!active) return
        setState(reg.active ? 'Página sin conexión disponible' : 'Preparando página sin conexión…')
        if (reg.waiting) reg.waiting.postMessage({ type: 'SKIP_WAITING' })
        if (!watchedRegistrations.has(reg)) {
          watchedRegistrations.add(reg)
          reg.addEventListener('updatefound', () => watchWorker(reg.installing))
        }
        watchWorker(reg.installing)
        await reg.update()
      } catch {
        if (active) setState('Registro fallido; la app sigue funcionando en línea')
      } finally { checking = false }
    }
    const visible = () => { void check() }
    let interval: number | undefined
    if (import.meta.env.PROD && 'serviceWorker' in navigator && window.isSecureContext) {
      navigator.serviceWorker.addEventListener('controllerchange', controllerChanged)
      document.addEventListener('visibilitychange', visible)
      window.addEventListener('focus', visible)
      interval = window.setInterval(visible, 60_000)
      void check()
    } else {
      queueMicrotask(() => { if (active) setState(import.meta.env.DEV ? 'Desarrollo: SW desactivado' : 'SW no disponible (requiere HTTPS)') })
    }
    return () => {
      active = false
      window.removeEventListener('beforeinstallprompt', prompt)
      window.removeEventListener('appinstalled', installed)
      media.removeEventListener('change', displayChanged)
      navigator.serviceWorker?.removeEventListener('controllerchange', controllerChanged)
      document.removeEventListener('visibilitychange', visible)
      window.removeEventListener('focus', visible)
      clearInterval(interval)
    }
  }, [])
  return {
    state, standalone, canInstall: !!installPrompt && !standalone,
    install: async () => { if (installPrompt) { await installPrompt.prompt(); await installPrompt.userChoice; setInstallPrompt(undefined) } },
  }
}
