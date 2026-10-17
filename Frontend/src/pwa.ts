import { useEffect, useState } from 'react'

type InstallPrompt = Event & { prompt(): Promise<void>; userChoice: Promise<{ outcome: string }> }
export function usePwa() {
  const [registration, setRegistration] = useState<ServiceWorkerRegistration>()
  const [waiting, setWaiting] = useState<ServiceWorker>()
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
    if (import.meta.env.PROD && 'serviceWorker' in navigator && window.isSecureContext) {
      void navigator.serviceWorker.register('/sw.js', { updateViaCache: 'none' }).then(reg => {
        if (!active) return
        setRegistration(reg)
        setState(reg.active ? 'Shell disponible sin conexión' : 'Preparando shell sin conexión…')
        if (reg.waiting) setWaiting(reg.waiting)
        reg.addEventListener('updatefound', () => {
          const worker = reg.installing
          worker?.addEventListener('statechange', () => {
            if (!active) return
            if (worker.state === 'installed') {
              setState('Shell disponible sin conexión')
              if (navigator.serviceWorker.controller) setWaiting(worker)
            }
            if (worker.state === 'redundant') setState('Shell no disponible; la app sigue funcionando')
          })
        })
      }).catch(() => { if (active) setState('Registro fallido; la app sigue funcionando en línea') })
    } else {
      queueMicrotask(() => { if (active) setState(import.meta.env.DEV ? 'Desarrollo: SW desactivado' : 'SW no disponible (requiere HTTPS)') })
    }
    return () => {
      active = false
      window.removeEventListener('beforeinstallprompt', prompt)
      window.removeEventListener('appinstalled', installed)
      media.removeEventListener('change', displayChanged)
    }
  }, [])
  useEffect(() => {
    if (!registration) return
    const check = () => { if (document.visibilityState === 'visible') void registration.update().catch(() => {}) }
    document.addEventListener('visibilitychange', check)
    const interval = window.setInterval(check, 60_000)
    return () => { clearInterval(interval); document.removeEventListener('visibilitychange', check) }
  }, [registration])
  return {
    state, waiting, standalone, canInstall: !!installPrompt && !standalone,
    install: async () => { if (installPrompt) { await installPrompt.prompt(); await installPrompt.userChoice; setInstallPrompt(undefined) } },
    refresh: () => {
      if (!waiting) return
      navigator.serviceWorker.addEventListener('controllerchange', () => window.location.reload(), { once: true })
      waiting.postMessage({ type: 'SKIP_WAITING' })
    },
  }
}
