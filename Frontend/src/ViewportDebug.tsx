import { useEffect, useState } from 'react'

function snapshot() {
  const width = window.innerWidth
  return {
    viewport: `${width} × ${window.innerHeight} CSS px`,
    visualViewport: window.visualViewport ? `${Math.round(window.visualViewport.width)} × ${Math.round(window.visualViewport.height)} · offset ${Math.round(window.visualViewport.offsetTop)}` : 'No disponible',
    dpr: window.devicePixelRatio,
    orientation: screen.orientation?.type ?? (width > window.innerHeight ? 'landscape' : 'portrait'),
    breakpoint: width >= 1536 ? '2xl' : width >= 1280 ? 'xl' : width >= 1024 ? 'lg' : width >= 768 ? 'md' : width >= 640 ? 'sm' : 'base',
    mode: matchMedia('(display-mode: standalone)').matches ? 'standalone' : 'browser',
    secure: window.isSecureContext ? 'sí' : 'no',
    sw: 'serviceWorker' in navigator ? navigator.serviceWorker.controller?.state ?? 'sin controlador (dev: SW desactivado)' : 'no soportado',
  }
}
export default function ViewportDebug() {
  const [data, setData] = useState(snapshot)
  const [assets, setAssets] = useState('Comprobando manifest e iconos…')
  const [worker, setWorker] = useState('Comprobando registro…')
  useEffect(() => {
    let active = true
    const update = () => {
      setData(snapshot())
      if ('serviceWorker' in navigator) void navigator.serviceWorker.getRegistration().then(reg => { if (active) setWorker(reg ? `active: ${reg.active?.state ?? '—'}; waiting: ${reg.waiting?.state ?? '—'}; installing: ${reg.installing?.state ?? '—'}` : 'Sin registro (esperado en desarrollo)') }).catch(() => { if (active) setWorker('Registro no accesible') })
    }
    const media = matchMedia('(display-mode: standalone)')
    window.addEventListener('resize', update); window.visualViewport?.addEventListener('resize', update); window.addEventListener('orientationchange', update); media.addEventListener('change', update)
    const interval = window.setInterval(update, 1000)
    void (async () => {
      try {
        const response = await fetch('/manifest.webmanifest', { cache: 'no-cache' })
        if (!response.ok) throw new Error('Manifest no disponible')
        const manifest = await response.json() as { icons: { src: string; sizes: string }[] }
        const results = await Promise.all(manifest.icons.map(async icon => {
          const image = new Image(); image.src = icon.src; await image.decode()
          return `${icon.src}: ${image.naturalWidth}×${image.naturalHeight} (${icon.sizes}) ✓`
        }))
        if (active) setAssets(`Manifest ✓\n${results.join('\n')}`)
      } catch { if (active) setAssets('Manifest o iconos no resuelven ✗') }
    })()
    update()
    return () => { active = false; clearInterval(interval); window.removeEventListener('resize', update); window.visualViewport?.removeEventListener('resize', update); window.removeEventListener('orientationchange', update); media.removeEventListener('change', update) }
  }, [])
  return <section className="panel debug"><h2>Diagnóstico en vivo · solo desarrollo</h2><p>Abre esta ruta en ambos dispositivos, gira la pantalla y comunica estos valores. No necesita backend. Para probar instalación y SW usa la compilación de producción.</p><dl>{Object.entries(data).map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{value}</dd></div>)}</dl><h3>Service worker</h3><p>{worker}</p><h3>Manifest e iconos</h3><pre>{assets}</pre><p>Escala: base &lt;640 · sm 640 · md 768 · lg 1024 · xl 1280 · 2xl 1536. Shell máx. 1600px.</p></section>
}
