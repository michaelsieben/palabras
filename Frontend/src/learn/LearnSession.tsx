import { useEffect, useReducer, useRef, useState } from 'react'
import { getSessionPlan } from '../api/learn'
import type { Attempt, Rating } from '../api/learn'
import { isSlowAnswer } from '../copy/feedback'
import { initialSessionState, sessionReducer } from './sessionMachine'
import { createMemoryEnqueue } from './memoryAttempts'
import type { EnqueueAttempt } from './memoryAttempts'
import { PromptCard } from './PromptCard'
import { TypedAnswer } from './TypedAnswer'
import { GradeButtons } from './GradeButtons'
import { FeedbackPanel } from './FeedbackPanel'
import { SessionSummary } from './SessionSummary'
import { EmptyQueue } from './EmptyQueue'
import { CopyText } from './CopyText'

export function LearnSession({ onComplete, enqueue: injectedEnqueue }: { onComplete(): void; enqueue?: EnqueueAttempt }) {
  const [state, dispatch] = useReducer(sessionReducer, initialSessionState)
  const [memoryEnqueue] = useState(() => createMemoryEnqueue())
  const enqueue = injectedEnqueue ?? memoryEnqueue
  const [sendErrors, setSendErrors] = useState<Attempt[]>([])
  const [pending, setPending] = useState(0)
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
    void getSessionPlan({}, controller.signal).then(plan => { if (!controller.signal.aborted) dispatch({ type: 'loaded', plan }) }).catch(error => {
      if (!controller.signal.aborted) dispatch({ type: 'failed', message: error instanceof Error ? error.message : 'API unavailable' })
    })
    return () => controller.abort()
  }, [round])
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
      setPending(count => count + 1)
      void enqueue(attempt).then(result => {
        if (result.card?.leech) setNotice('Palabra en pausa: se ha repetido varias veces. Puedes recuperarla desde Palabras cuando esa opción esté disponible.')
        else if (!['applied', 'duplicate'].includes(result.status)) setNotice('Una respuesta no cambió el repaso: la tarjeta cambió o ya recibió una respuesta más reciente. La próxima vuelta traerá el estado actual.')
      }).catch(() => setSendErrors(errors => [...errors, attempt])).finally(() => setPending(count => count - 1))
    }
  }, [state.attempts, enqueue])
  async function retryAnswers() {
    const attempts = sendErrors
    setSendErrors([])
    setPending(count => count + attempts.length)
    await Promise.all(attempts.map(async attempt => {
      try { await enqueue(attempt) } catch { setSendErrors(errors => [...errors, attempt]) }
      finally { setPending(count => count - 1) }
    }))
  }
  const item = state.queue[0]
  function rate(rating?: Rating, override = false) {
    dispatch({ type: 'rate', rating, override, clientAttemptId: crypto.randomUUID(), reviewedAtUtc: new Date().toISOString(),
      durationMs: item?.mode === 'recognition' ? revealDuration.current : answerDuration.current })
  }
  return <section className="session-pane learn-session" aria-label="Sesión de práctica" ref={pane} tabIndex={-1}>
    {state.plan && <div className="summary" aria-label="Cola de hoy"><div><strong>{state.plan.counts.due} repasos</strong><span>{state.plan.counts.newOffered} nuevas ofrecidas</span></div><div><strong>{state.attempts.length} respuestas</strong><span>{state.queue.length} tarjetas por practicar</span></div></div>}
    {state.plan?.warnings.includes('backlog_no_new') && <aside className="panel session-notice"><CopyText kind="backlog-warning" values={{ n: state.plan.counts.backlog }} /></aside>}
    <p className="secondary-text" role="status">{pending ? `Enviando ${pending} respuestas…` : state.attempts.length && !sendErrors.length ? 'Respuestas enviadas.' : ''}</p>
    {sendErrors.length > 0 && <aside className="panel session-notice" role="status"><p>{sendErrors.length} respuestas pendientes de envío. Solo se conservan mientras esta vista siga abierta; no la abandones, cierres ni actualices la página.</p><button onClick={() => void retryAnswers()}>Reintentar envío</button></aside>}
    {notice && <p className="note" role="status">{notice}</p>}
    {state.stopSuggested && !state.stopping && <aside className="panel session-notice"><CopyText kind="stop-suggestion" values={{ k: Math.min(8, state.attempts.length) }} /><button onClick={() => dispatch({ type: 'stop' })}>Parar tras recuperar las pendientes</button></aside>}
    {state.stopping && state.phase !== 'summary' && <p className="note">Recupera estas tarjetas antes de terminar. Sin prisa; también puedes consultar la respuesta.</p>}
    {['idle', 'loading'].includes(state.phase) && <p role="status">Preparando tu repaso…</p>}
    {state.phase === 'error' && <section className="panel"><h2>No se ha podido preparar el repaso</h2><p role="alert">Comprueba la conexión o vuelve a iniciar sesión e inténtalo de nuevo.</p><button onClick={() => setRound(value => value + 1)}>Reintentar</button></section>}
    {state.phase === 'empty' && <EmptyQueue nextDueUtc={state.plan?.nextDueUtc} retry={() => setRound(value => value + 1)} />}
    {state.phase === 'summary' && <SessionSummary attempts={state.attempts} onAnotherRound={() => setRound(value => value + 1)} />}
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
