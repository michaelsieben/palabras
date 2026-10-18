import { useEffect, useState } from 'react'
import { getForecast } from '../api/learn'
import type { Attempt, Forecast } from '../api/learn'
import { sessionTotals } from './sessionMachine'
import { CopyText } from './CopyText'

export function SessionSummary({ attempts, pending, savingFailed = false, onAnotherRound }: { attempts: readonly Attempt[]; pending: number; savingFailed?: boolean; onAnotherRound(): void }) {
  const [forecast, setForecast] = useState<Forecast | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    void getForecast(controller.signal).then(value => { if (!controller.signal.aborted) setForecast(value) }).catch(() => undefined)
    return () => controller.abort()
  }, [pending])
  return <section className="panel session-summary"><CopyText kind="summary-line-1" values={sessionTotals(attempts)} />
    {forecast ? <><CopyText kind="summary-line-2" values={{ tomorrow: forecast.tomorrow, week: forecast.next7Days.reduce((sum, day) => sum + day.due, 0) }} />{pending > 0 && <p className="note">Estimación anterior al envío de las respuestas pendientes.</p>}</> : <p className="note">Previsión disponible al conectar.</p>}
    <p>{attempts.length} respuestas en esta sesión, incluidos los repasos repetidos.</p>
    <button className="primary" disabled={savingFailed} onClick={onAnotherRound}>Otra vuelta</button>
  </section>
}
