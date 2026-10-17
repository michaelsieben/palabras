import { useEffect, useState } from 'react'

type Health = 'checking' | 'ok' | 'error'
const labels: Record<Health, string> = {
  checking: 'Comprobando conexión…',
  ok: 'API conectada',
  error: 'API no disponible',
}

export default function App() {
  const [health, setHealth] = useState<Health>('checking')

  useEffect(() => {
    const controller = new AbortController()
    async function checkHealth() {
      try {
        const response = await fetch('/api/ping', { signal: controller.signal })
        const data: unknown = await response.json()
        if (!response.ok || typeof data !== 'object' || data === null || !('status' in data) || data.status !== 'ok') {
          throw new Error('API health check failed')
        }
        setHealth('ok')
      } catch {
        if (!controller.signal.aborted) {
          setHealth('error')
        }
      }
    }
    void checkHealth()
    return () => controller.abort()
  }, [])

  return (
    <main>
      <header>
        <p className="eyebrow">Español de España · A2</p>
        <h1>Palabras</h1>
        <p>Tu vocabulario, paso a paso.</p>
      </header>
      <section aria-labelledby="scaffold-heading">
        <h2 id="scaffold-heading">Estamos preparando tu espacio</h2>
        <p>La práctica y la revisión llegarán en próximas versiones.</p>
        <p className={`badge ${health}`} role="status">{labels[health]}</p>
      </section>
    </main>
  )
}
