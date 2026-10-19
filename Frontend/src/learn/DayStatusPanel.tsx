import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { ApiError } from '../api/client'
import { getDayStatus, skipDay } from '../api/learn'
import type { DayStatus } from '../api/learn'
import type { getCompletionQueue } from './completionQueue'

function dayLabel(status: DayStatus['status']): string {
  return status === 'done' ? 'Día completado' : status === 'skipped' ? 'Hoy saltado' : status === 'missed' ? 'Hoy puedes empezar otra racha' : 'Hoy, por completar'
}
function formatDay(date: string): string {
  return new Intl.DateTimeFormat('es-ES', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(`${date}T12:00:00Z`))
}

export function DayStatusPanel({ completionQueue }: { completionQueue: ReturnType<typeof getCompletionQueue> }) {
  const completion = useSyncExternalStore(completionQueue.subscribe, completionQueue.getSnapshot)
  const completionKey = JSON.stringify(completion.last)
  const [day, setDay] = useState<DayStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [online, setOnline] = useState(navigator.onLine)
  const [skipping, setSkipping] = useState(false)
  const [notice, setNotice] = useState('')
  const requestId = useRef(0)
  const refresh = useCallback((signal?: AbortSignal) => {
    const id = ++requestId.current
    const current = () => !signal?.aborted && id === requestId.current
    return getDayStatus(signal).then(status => {
      if (current()) { setDay(status); setError(false) }
    }).catch(() => { if (current()) setError(true) })
      .finally(() => { if (current()) setLoading(false) })
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void refresh(controller.signal)
    const update = (silent = false) => { setOnline(navigator.onLine); if (navigator.onLine) { if (!silent) setLoading(true); void refresh(controller.signal) } }
    const activeUpdate = () => update()
    const visible = () => { if (document.visibilityState === 'visible') update() }
    const timer = window.setInterval(() => { if (document.visibilityState === 'visible') update(true) }, 60_000)
    window.addEventListener('online', activeUpdate); window.addEventListener('offline', activeUpdate)
    window.addEventListener('focus', activeUpdate); document.addEventListener('visibilitychange', visible)
    return () => {
      controller.abort(); window.clearInterval(timer); window.removeEventListener('online', activeUpdate); window.removeEventListener('offline', activeUpdate)
      window.removeEventListener('focus', activeUpdate); document.removeEventListener('visibilitychange', visible)
    }
  }, [refresh, completionKey]) // Done's payload can be yesterday: refetch today only when its data changes.
  async function skip() {
    if (!day || skipping) return
    ++requestId.current // An older status read must not overwrite this mutation.
    setSkipping(true); setNotice('')
    try {
      const result = await skipDay(day.localDate)
      if (result.kind === 'skipped') { setDay(result.day); setNotice('Salto guardado. Puedes practicar igualmente; al completar el día recuperas el salto.'); await refresh() }
      else if (result.kind === 'already_done') {
        setDay({ ...day, status: 'done' }); setNotice('Ya has completado hoy. Puedes seguir practicando.'); await refresh()
      } else {
        setDay({ ...day, skipAvailable: false, skipNextAvailable: result.nextAvailable })
        setNotice(`Ya has usado el salto. Puedes volver a saltar el ${formatDay(result.nextAvailable)}. Mientras tanto, puedes practicar.`)
        await refresh()
      }
    } catch (failure) {
      setNotice(failure instanceof ApiError && failure.status === 400 ? 'Ha cambiado el día. Actualizamos la fecha; vuelve a intentarlo.' : 'No se ha confirmado el salto. Conéctate y vuelve a intentarlo; puedes seguir practicando.')
      await refresh()
    } finally { setSkipping(false) }
  }
  return <section className="panel day-status" aria-label="Día y racha">
    <div className="day-status-heading">
      <div role="status" aria-atomic="true">{day ? <><strong>{dayLabel(day.status)}</strong><span>{formatDay(day.localDate)} · Racha: {day.streak} {day.streak === 1 ? 'día' : 'días'}</span></> : <strong>{loading ? 'Consultando día y racha…' : 'Día y racha sin confirmar'}</strong>}</div>
      {day && !['done', 'skipped'].includes(day.status ?? '') && <button disabled={!online || loading || error || skipping || !day.skipAvailable || completion.pending > 0} onClick={() => void skip()}>{skipping ? 'Guardando salto…' : 'Saltar hoy'}</button>}
    </div>
    {day?.status === 'skipped' ? <p className="note">El salto conserva la racha, no añade un día. Puedes seguir practicando.</p> : day && !['done', 'skipped'].includes(day.status ?? '') && day.skipAvailable && <p className="note">Un salto cada 7 días conserva la racha. No cambia tus repasos.</p>}
    {day && !day.skipAvailable && day.skipNextAvailable && <p className="note">Próximo salto: {formatDay(day.skipNextAvailable)}.</p>}
    {loading && day && <p className="note" role="status">Actualizando día y racha…</p>}
    {(error || !online) && <div role="status"><p className="note">{online ? 'No se ha podido actualizar día y racha. Puedes seguir practicando.' : 'Sin conexión: día y racha sin actualizar. Puedes seguir practicando.'}</p><button onClick={() => { setLoading(true); void refresh() }}>Actualizar día y racha</button></div>}
    {notice && <p className="note" role="status">{notice}</p>}
    {completion.pending > 0 && <p className="note" role="status">{completion.pending} {completion.pending === 1 ? 'día completado guardado' : 'días completados guardados'} en este dispositivo, pendiente de envío. Se enviará al conectar, después de las respuestas.</p>}
    {completion.error && <div role="status"><p className="note">El día aún no se ha confirmado. Lo guardado se reintentará al conectar.</p><button onClick={completionQueue.trigger}>Reintentar día completado</button></div>}
    {completion.rejected > 0 && <p className="note" role="status">No se pudo registrar un día completado: la fecha ya no es válida. Solo se aceptan hoy y ayer. Las respuestas se envían igualmente; puedes practicar hoy para completar este día.</p>}
  </section>
}
