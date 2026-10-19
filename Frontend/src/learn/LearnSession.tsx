import { useEffect, useMemo, useReducer, useRef, useState, useSyncExternalStore } from 'react'
import { loadCachedPlan } from './cachedPlan'
import type { Attempt, Rating } from '../api/learn'
import { isSlowAnswer } from '../copy/feedback'
import { initialSessionState, sessionReducer } from './sessionMachine'
import type { getAttemptQueue } from './attemptQueue'
import type { EnqueueAttempt } from './memoryAttempts'
import { PromptCard } from './PromptCard'
import { TypedAnswer } from './TypedAnswer'
import { GradeButtons } from './GradeButtons'
import { FeedbackPanel } from './FeedbackPanel'
import { SessionSummary } from './SessionSummary'
import { EmptyQueue } from './EmptyQueue'
import { CopyText } from './CopyText'
import { sessionDayCompletion, countsAsCompletedDay, getCompletionQueue } from './completionQueue'
import type { DayCompletion } from '../api/learn'
import { DayStatusPanel } from './DayStatusPanel'

export function LearnSession({ username, attemptQueue, onComplete, enqueue: injectedEnqueue }: {
  username: string; attemptQueue: ReturnType<typeof getAttemptQueue>; onComplete(): void; enqueue?: EnqueueAttempt
}) {
  const [state, dispatch] = useReducer(sessionReducer, initialSessionState)
  const enqueue = injectedEnqueue ?? attemptQueue.enqueue
  const completionQueue = useMemo(() => getCompletionQueue(username, attemptQueue), [username, attemptQueue])
  const completionStatus = useSyncExternalStore(completionQueue.subscribe, completionQueue.getSnapshot)
  const completionBody = useRef<DayCompletion | null>(null)
  const [completionSaveError, setCompletionSaveError] = useState(false)
  const [completionDate, setCompletionDate] = useState<string | null>(null)
  const queueStatus = useSyncExternalStore(attemptQueue.subscribe, attemptQueue.getSnapshot)
  const [online, setOnline] = useState(navigator.onLine)
  useEffect(() => {
    const update = () => setOnline(navigator.onLine)
    window.addEventListener('online', update); window.addEventListener('offline', update)
    return () => { window.removeEventListener('online', update); window.removeEventListener('offline', update) }
  }, [])
  const [sendErrors, setSendErrors] = useState<Attempt[]>([])
  const [delivered, setDelivered] = useState(() => new Set<string>())
  const [notice, setNotice] = useState('')
  const [slowReveal, setSlowReveal] = useState(false)
  const submitted = useRef(new Set<string>())
  const startedAt = useRef(0)
  const revealDuration = useRef(0)
  const answerDuration = useRef(0)
  const pane = useRef<HTMLElement>(null)
  const complete = useRef(onComplete)
  useEffect(() => { complete.current = onComplete }, [onComplete])
  const [round, setRound] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    dispatch({ type: 'load' })
    completionBody.current = null
    void loadCachedPlan(username, controller.signal).then(({ plan, offline }) => {
      if (!controller.signal.aborted) dispatch(offline ? { type: 'failed', message: 'offline', cachedPlan: plan } : { type: 'loaded', plan })
    }).catch(error => {
      if (!controller.signal.aborted) dispatch({ type: 'failed', message: error instanceof Error ? error.message : 'API unavailable' })
    })
    return () => controller.abort()
  }, [round, username])
  useEffect(() => {
    if (state.phase === 'next') dispatch({ type: 'next' })
    if (state.phase === 'prompting') { startedAt.current = performance.now(); revealDuration.current = 0 }
    if (state.phase === 'summary') complete.current()
    if (['prompting', 'grading', 'feedback', 'summary', 'empty', 'error'].includes(state.phase)) {
      // Input owns typed focus; headings orient recognition/feedback keyboard users.
      if (state.phase !== 'prompting' || state.queue[0]?.mode === 'recognition') pane.current?.focus()
    }
  }, [state.phase, state.queue])
  useEffect(() => {
    for (const attempt of state.attempts) {
      if (submitted.current.has(attempt.clientAttemptId)) continue
      submitted.current.add(attempt.clientAttemptId)
      void enqueue(attempt).then(result => {
        setDelivered(ids => new Set(ids).add(result.clientAttemptId))
        if (result.card?.leech) setNotice('Palabra en pausa: se ha repetido varias veces. Puedes recuperarla desde Palabras cuando esa opción esté disponible.')
        else if (!['applied', 'duplicate'].includes(result.status)) setNotice('Una respuesta no cambió el repaso: la tarjeta cambió o ya recibió una respuesta más reciente. La próxima vuelta traerá el estado actual.')
      }).catch(() => setSendErrors(errors => [...errors, attempt]))
    }
  }, [state.attempts, enqueue])
  // Runs after enqueue effect: flush waits for durable writes, not UI progression.
  useEffect(() => { if (state.phase === 'summary') attemptQueue.trigger() }, [state.phase, attemptQueue])
  useEffect(() => {
    if (state.phase !== 'summary' || !state.plan || !state.attempts.length || !countsAsCompletedDay(state.stopping, state.attempts)) return
    // Capture the server's session date before waiting for storage/network.
    completionBody.current ??= sessionDayCompletion(state.plan, state.attempts)
    const body = completionBody.current
    void attemptQueue.persisted().then(() => completionQueue.enqueue(body))
      .then(() => { setCompletionSaveError(false); setCompletionDate(body.localDate) }).catch(() => setCompletionSaveError(true))
  }, [state.phase, state.plan, state.attempts, state.stopping, attemptQueue, completionQueue, sendErrors])
  async function retryCompletion() {
    try {
      await attemptQueue.persisted()
      if (completionBody.current) await completionQueue.enqueue(completionBody.current)
      setCompletionDate(completionBody.current?.localDate ?? null)
      setCompletionSaveError(false)
    } catch { setCompletionSaveError(true) }
  }
  async function retryAnswers() {
    const attempts = sendErrors
    setSendErrors([])
    const jobs = attempts.map(async attempt => {
      try { const result = await enqueue(attempt); setDelivered(ids => new Set(ids).add(result.clientAttemptId)) }
      catch { setSendErrors(errors => [...errors, attempt]) }
    })
    attemptQueue.trigger()
    await Promise.all(jobs)
  }
  const item = state.queue[0]
  function rate(rating?: Rating, override = false) {
    dispatch({ type: 'rate', rating, override, clientAttemptId: crypto.randomUUID(), reviewedAtUtc: new Date().toISOString(),
      durationMs: item?.mode === 'recognition' ? revealDuration.current : answerDuration.current })
  }
  return <section className="session-pane learn-session" aria-label="Sesión de práctica" ref={pane} tabIndex={-1}>
    <DayStatusPanel completionQueue={completionQueue} />
    {state.plan && <div className="summary" aria-label="Cola de hoy"><div><strong>{state.plan.counts.due} repasos</strong><span>{state.plan.counts.newOffered} nuevas ofrecidas</span></div><div><strong>{state.attempts.length} respuestas</strong><span>{state.queue.length} tarjetas por practicar</span></div></div>}
    {state.plan?.warnings.includes('backlog_no_new') && <aside className="panel session-notice"><CopyText kind="backlog-warning" values={{ n: state.plan.counts.backlog }} /></aside>}
    {state.offline && <p className="note">Repaso sin conexión: última cola guardada. Las tarjetas pueden haber cambiado; se comprobarán al enviar.</p>}
    {queueStatus.pending > 0 && !online ? <div className="secondary-text" role="status"><CopyText kind="offline-pending" values={{ n: queueStatus.pending }} /></div>
      : <p className="secondary-text" role="status">{queueStatus.pending ? `${queueStatus.pending} respuestas guardadas en este dispositivo, pendientes de envío.`
        : state.attempts.length && state.attempts.every(attempt => delivered.has(attempt.clientAttemptId)) && !sendErrors.length ? 'Respuestas enviadas.' : ''}</p>}
    {queueStatus.error && <aside className="panel session-notice" role="status"><p>No se ha podido enviar. Las respuestas guardadas se reintentarán al reconectar o terminar otra sesión.</p><button onClick={attemptQueue.trigger}>Reintentar envío</button></aside>}
    {sendErrors.length > 0 && <aside className="panel session-notice" role="alert"><p>{sendErrors.length} respuestas no se pudieron guardar en este dispositivo. No cierres ni actualices esta vista hasta reintentar.</p><button onClick={() => void retryAnswers()}>Reintentar guardado</button></aside>}
    {completionSaveError && <aside className="panel session-notice" role="alert"><p>No se pudo guardar el día completado. No cierres ni actualices esta vista. Reintenta primero las respuestas que no se guardaron.</p><button onClick={() => void retryCompletion()}>Guardar día completado</button></aside>}
    {notice && <p className="note" role="status">{notice}</p>}
    {state.stopSuggested && !state.stopping && <aside className="panel session-notice"><CopyText kind="stop-suggestion" values={{ k: Math.min(8, state.attempts.length) }} /><button onClick={() => dispatch({ type: 'stop' })}>Parar tras recuperar las pendientes</button></aside>}
    {state.stopping && state.phase !== 'summary' && <p className="note">Recupera estas tarjetas antes de terminar. Sin prisa; también puedes consultar la respuesta.</p>}
    {['idle', 'loading'].includes(state.phase) && <p role="status">Preparando tu repaso…</p>}
    {state.phase === 'error' && <section className="panel"><h2>No se ha podido preparar el repaso</h2><p role="alert">Comprueba la conexión o vuelve a iniciar sesión e inténtalo de nuevo.</p><button onClick={() => setRound(value => value + 1)}>Reintentar</button></section>}
    {state.phase === 'empty' && <EmptyQueue nextDueUtc={state.plan?.nextDueUtc} retry={() => setRound(value => value + 1)} />}
    {state.phase === 'summary' && <><p className="note" role="status">{!countsAsCompletedDay(state.stopping, state.attempts) ? 'Sesión terminada antes de completar el día. Tus respuestas se guardan; puedes continuar con otra vuelta.' : completionStatus.last && completionStatus.last.localDate === completionDate ? `Día completado · Racha: ${completionStatus.last.streak}` : 'Sesión terminada. El día se confirmará después de enviar las respuestas.'}</p><SessionSummary attempts={state.attempts} pending={queueStatus.pending} savingFailed={completionSaveError || sendErrors.length > 0 || (countsAsCompletedDay(state.stopping, state.attempts) && completionDate === null)} onAnotherRound={() => { setCompletionDate(null); setRound(value => value + 1) }} /></>}
    {item && ['prompting', 'answering', 'grading', 'feedback'].includes(state.phase) && <>
      <PromptCard item={item} revealed={state.phase === 'grading'} />
      {item.mode === 'recognition' ? state.phase === 'grading'
        ? <GradeButtons onRate={rate} slow={slowReveal} />
        : <div className="session-actions"><button className="primary reveal" onClick={() => { revealDuration.current = performance.now() - startedAt.current; setSlowReveal(isSlowAnswer(revealDuration.current, 'recognition')); dispatch({ type: 'reveal' }) }}>Mostrar respuesta</button></div>
        : state.phase === 'feedback' && state.feedback ? <FeedbackPanel item={item} result={state.feedback} onRate={rate} />
        : <TypedAnswer key={`${state.attempts.length}-${item.cardId}`} answer={state.answer} onChange={value => dispatch({ type: 'answer', value })} onSubmit={() => { answerDuration.current = performance.now() - startedAt.current; dispatch({ type: 'submit', durationMs: answerDuration.current }) }} />}
    </>}
  </section>
}
