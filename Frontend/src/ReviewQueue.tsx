import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import type { AlternativeField, ReviewAction, ReviewItem, ReviewQueueResponse } from './api/review'
import { getReviewQueueStore } from './review/reviewQueue'
import { getCachedQueue } from './review/cachedQueue'
import { batchEligible, parseSittingSize, sittingKey, toggleBatch } from './review/reviewModel'
import { ReviewEditor } from './review/ReviewEditor'
import { ReviewRowStatus } from './review/ReviewRowStatus'
import { ApprovedPanel } from './review/ApprovedPanel'

const labels: Record<AlternativeField, string> = { gloss: 'Traducción', example: 'Ejemplo', lemma: 'Palabra', regional_variant: 'Variante regional (solo lectura)', register_variant: 'Variante de registro (solo lectura)' }
export function ReviewQueue({ approved, username, queue, onComplete }: { approved: boolean; username: string; queue: ReturnType<typeof getReviewQueueStore>; onComplete(): void }) {
  return approved ? <ApprovedPanel key={username} queue={queue} /> : <PendingPanel key={username} username={username} queue={queue} onComplete={onComplete} />
}
function PendingPanel({ username, queue, onComplete }: { username: string; queue: ReturnType<typeof getReviewQueueStore>; onComplete(): void }) {
  const snapshot = useSyncExternalStore(queue.subscribe, queue.getSnapshot)
  const cache = useMemo(() => getCachedQueue(username), [username])
  const [data, setData] = useState<ReviewQueueResponse>()
  const [loading, setLoading] = useState(true)
  const [offline, setOffline] = useState(false)
  const [error, setError] = useState('')
  const [cacheFailed, setCacheFailed] = useState(false)
  const [size, setSize] = useState(() => { try { return parseSittingSize(localStorage.getItem(sittingKey(username))) } catch { return 15 } })
  const [done, setDone] = useState(0)
  const [finished, setFinished] = useState(false)
  const [editing, setEditing] = useState<number>()
  const [selected, setSelected] = useState<ReviewItem>()
  const [field, setField] = useState<AlternativeField>('gloss')
  const [sense, setSense] = useState(0)
  const [flagging, setFlagging] = useState(false)
  const [busy, setBusy] = useState(false)
  const [rowErrors, setRowErrors] = useState<Record<number, string>>({})
  const [checked, setChecked] = useState<number[]>([])
  const [message, setMessage] = useState('')
  const dialog = useRef<HTMLDialogElement>(null)
  const opener = useRef<HTMLElement | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  const requestId = useRef(0)
  const mutationBusy = useRef(false)
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const id = ++requestId.current
    try {
      await queue.flush()
      if (signal?.aborted || id !== requestId.current) return undefined
      setLoading(true)
      const acknowledged = queue.getSnapshot().rows.filter(r => r.state === 'applied')
      const result = await cache.load(signal)
      if (signal?.aborted || id !== requestId.current) return undefined
      setData(result.data); setOffline(result.offline); setCacheFailed(result.cacheFailed); setError('')
      if (!result.offline) for (const row of acknowledged) await queue.discard(row.action.clientReviewId)
      return result.offline ? undefined : result.data
    } catch {
      if (!signal?.aborted && id === requestId.current) setError('No se ha podido cargar la cola. Conéctate y vuelve a intentarlo.')
      return undefined
    } finally { if (!signal?.aborted && id === requestId.current) setLoading(false) }
  }, [cache, queue])
  useEffect(() => {
    const controller = new AbortController()
    // Hydrate from external storage/network asynchronously; StrictMode cleanup cancels the read.
    queueMicrotask(() => { if (!controller.signal.aborted) void refresh(controller.signal) })
    return () => controller.abort()
  }, [refresh])
  useEffect(() => { if (selected) { if (!dialog.current?.open) dialog.current?.showModal() } else dialog.current?.close() }, [selected])
  function dismiss() { setSelected(undefined); setFlagging(false); requestAnimationFrame(() => opener.current?.focus()) }
  async function resolve(word: ReviewItem, action: Omit<ReviewAction, 'clientReviewId' | 'wordId' | 'wordVersion'>) {
    if (mutationBusy.current) return false
    mutationBusy.current = true
    setBusy(true)
    try {
      await queue.enqueue({ ...action, clientReviewId: crypto.randomUUID(), wordId: word.wordId, wordVersion: word.version }, word)
      setDone(n => n + 1); setEditing(undefined); dismiss(); setChecked(ids => ids.filter(id => id !== word.wordId)); setMessage(`${word.headword}: decisión guardada aquí; pendiente de confirmación.`)
      onComplete(); requestAnimationFrame(() => heading.current?.focus())
      return true
    } catch { setRowErrors(e => ({ ...e, [word.wordId]: 'No se ha guardado la decisión. Reintenta antes de cerrar esta vista.' })); return false }
    finally { mutationBusy.current = false; setBusy(false) }
  }
  async function renew(id: string, wordId: number) {
    const fresh = await refresh()
    if (fresh?.items.some(w => w.wordId === wordId)) { await queue.discard(id).catch(() => setRowErrors(e => ({ ...e, [wordId]: 'No se pudo recuperar la decisión local.' }))); setMessage('Palabra actualizada. Revisa el contenido antes de decidir de nuevo.') }
    else setRowErrors(e => ({ ...e, [wordId]: 'No está en las primeras 30 pendientes. La decisión sigue guardada; puedes reintentar o descartarla.' }))
  }
  const actionRows = snapshot.rows.filter(r => r.action.action !== 'demote' && !('gloss' in r.word) && r.word.status !== 'approved' && r.word.status !== 'rejected')
  const blocked = new Set(actionRows.map(r => r.word.wordId))
  const pending = (data?.items ?? []).filter(w => !blocked.has(w.wordId))
  const visible = finished ? [] : pending.slice(0, Math.max(0, size - done))
  const eligible = visible.filter(batchEligible)
  const topics = [...new Set(eligible.map(w => w.topic ?? 'Sin tema'))]
  const selectedEligible = eligible.filter(w => checked.includes(w.wordId)).slice(0, 25)
  async function batchApprove() {
    if (mutationBusy.current) return
    mutationBusy.current = true; setBusy(true)
    try {
      await queue.enqueueMany(selectedEligible.map(word => ({ word, action: { clientReviewId: crypto.randomUUID(), wordId: word.wordId, wordVersion: word.version, action: 'approve', viaBatch: true } })))
      setDone(n => n + selectedEligible.length); setChecked([]); setMessage(`${selectedEligible.length} decisiones guardadas aquí; pendientes de confirmación.`); onComplete(); requestAnimationFrame(() => heading.current?.focus())
    } catch { setRowErrors(e => ({ ...e, ...Object.fromEntries(selectedEligible.map(w => [w.wordId, 'No se guardó el lote. Reintenta antes de cerrar esta vista.'])) })) }
    finally { mutationBusy.current = false; setBusy(false) }
  }
  function changeSize(value: string) {
    const next = parseSittingSize(value); setSize(next)
    try { localStorage.setItem(sittingKey(username), String(next)) } catch { setMessage('Tamaño cambiado para esta visita; no se pudo guardar el ajuste.') }
  }
  return <section className="review-section" aria-labelledby="queue-heading">
    <div className="queue-heading"><div><h2 id="queue-heading" ref={heading} tabIndex={-1}>{data ? `${data.pendingTotal} pendientes` : 'Tu cola de revisión'}</h2><p>Revisa a tu ritmo. Tu español tiene la última palabra.</p></div><label>Palabras por sesión<select value={size} onChange={e => changeSize(e.target.value)}>{Array.from({ length: 26 }, (_, i) => i + 5).map(n => <option key={n}>{n}</option>)}</select></label></div>
    <p className="review-status" role="status">{message}</p>
    {loading && <p role="status">Cargando cola…</p>}
    {(offline || cacheFailed || snapshot.storageError) && <p className="note" role="status">{offline ? 'Sin conexión: cola guardada de hasta 30 palabras. Las decisiones se enviarán al conectar.' : cacheFailed ? 'La cola no pudo guardarse para uso sin conexión.' : 'Almacenamiento sin confirmar. Conserva esta vista y reintenta.'}</p>}
    {error && <div className="note" role="status"><p>{error}</p><button onClick={() => void refresh()}>Actualizar cola</button></div>}
    {eligible.length > 0 && <section className="panel batch-review" aria-label="Aprobación por lote"><h3>Aprobar por tema</h3><p>Solo concordancia alta, sin avisos. Lee cada palabra y selecciona hasta 25.</p>{topics.map(topic => <fieldset key={topic}><legend>{topic}</legend>{eligible.filter(w => (w.topic ?? 'Sin tema') === topic).map(word => <label className="check-label" key={word.wordId}><input type="checkbox" checked={checked.includes(word.wordId)} disabled={busy || !checked.includes(word.wordId) && checked.length >= 25} onChange={() => setChecked(ids => toggleBatch(ids, word.wordId))} />{word.article} {word.headword}</label>)}</fieldset>)}<button className="primary" disabled={busy || !selectedEligible.length} onClick={() => void batchApprove()}>Aprobar seleccionadas ({selectedEligible.length})</button></section>}
    <div className="review-grid">{visible.map(word => <article className="panel review-card" key={word.wordId} aria-label={`${word.article ?? ''} ${word.headword}`.trim()}>
      <div className="card-top"><span className="eyebrow">{word.topic ?? 'Sin tema'} · {word.cefr}</span><div className="review-chips">{word.agreement !== undefined && <span className="chip">{Math.round(word.agreement * 100)} % concordancia</span>}{word.band && <span className="chip">{word.band === 'alta' ? 'Alta' : 'Media'}</span>}{word.reducedConfidence && <span className="chip">Consejo reducido</span>}{word.lintWarnings.map((warning, i) => <span key={i} className="chip review-failure">{warning}</span>)}</div></div>
      <span className="chip">Pendiente</span><h3>{word.article} {word.headword}{word.gender && <small> ({({ masculine: 'm.', feminine: 'f.', common: 'común', not_applicable: 'sin género' } as Record<string, string>)[word.gender] ?? word.gender})</small>}</h3>
      {editing === word.wordId ? <ReviewEditor word={word} busy={busy} save={edits => void resolve(word, { action: 'approve_edit', edits })} cancel={() => { setEditing(undefined); requestAnimationFrame(() => document.getElementById(`edit-${word.wordId}`)?.focus()) }} /> : <>
        {word.senses.map(s => <div className="review-sense" key={s.order}>{word.senses.length > 1 && <p className="eyebrow">Acepción {s.order + 1}</p>}<p className="gloss" lang="en">{s.gloss}</p><p>{s.exampleEs}</p><p className="secondary-text" lang="en">{s.exampleEn}</p>{s.notes && <p className="note">{s.notes}</p>}</div>)}
        {word.notes && <p className="note">{word.notes}</p>}{word.regionNotes && <p className="note">Regional: {word.regionNotes}</p>}{word.registerNotes && <p className="note">Registro: {word.registerNotes}</p>}{word.lastFlag && <p className="note">Última marca: {word.lastFlag.action}{word.lastFlag.note && ` · ${word.lastFlag.note}`}</p>}
        <div className="review-actions"><button className="primary" disabled={busy} onClick={() => void resolve(word, { action: 'approve' })}>Vale ✓</button><button className="again" disabled={busy} onClick={e => { opener.current = e.currentTarget; setField('gloss'); setSense(word.senses[0].order); setSelected(word) }}>No ✗</button><button id={`edit-${word.wordId}`} disabled={busy} onClick={() => setEditing(word.wordId)}>Editar ✎</button></div>
      </>}{rowErrors[word.wordId] && <p className="review-failure" role="status">{rowErrors[word.wordId]}</p>}
    </article>)}</div>
    {actionRows.length > 0 && <section className="review-outbox" aria-label="Decisiones de este dispositivo"><h3>Decisiones guardadas aquí</h3><div className="review-grid">{actionRows.map(row => <article className="panel" key={row.action.clientReviewId}><h4>{row.word.article} {row.word.headword}</h4><p className="secondary-text">Último estado leído: {row.word.status === 'approved' ? 'Aprobada' : row.word.status === 'rejected' ? 'Retirada' : 'Pendiente'} · versión {row.action.wordVersion} · {row.action.action}</p><ReviewRowStatus row={row} retry={() => void queue.retry(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [row.word.wordId]: 'No se pudo reintentar.' })))} refresh={() => void renew(row.action.clientReviewId, row.word.wordId)} discard={() => void queue.discard(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [row.word.wordId]: 'No se pudo descartar.' })))} />{rowErrors[row.word.wordId] && <p className="review-failure" role="status">{rowErrors[row.word.wordId]}</p>}</article>)}</div></section>}
    {!loading && !visible.length && !error && <div className="panel sitting-end"><h3>Listo por hoy</h3><p>{done} decisiones guardadas en esta sesión. Pendientes de envío no cuentan como confirmadas.</p>{!finished && <button onClick={() => { setDone(0); setChecked([]); void refresh() }}>¿Otras {size}?</button>}<button onClick={() => { setFinished(true); dismiss(); setMessage('Listo por hoy. Las decisiones guardadas siguen disponibles abajo.') }}>Listo por hoy</button>{finished && <button onClick={() => { setFinished(false); setDone(0); void refresh() }}>Continuar revisión</button>}</div>}
    <button className="review-refresh" disabled={loading || busy} onClick={() => void refresh()}>Actualizar cola</button>
    <dialog className="alternatives-dialog" ref={dialog} aria-labelledby="alternatives-title" onCancel={e => { e.preventDefault(); if (!busy) dismiss() }}>
      {selected && <><div className="drawer-heading"><div><p className="eyebrow">{selected.article} {selected.headword}</p><h2 id="alternatives-title">{flagging ? 'Cambiar ⚑' : '¿Qué encaja mejor?'}</h2></div><button autoFocus disabled={busy} onClick={dismiss} aria-label="Cerrar alternativas">✕</button></div>
        {flagging ? <form onSubmit={e => { e.preventDefault(); const form = new FormData(e.currentTarget); void resolve(selected, { action: 'needs_change', field: 'whole', note: String(form.get('note') ?? '') }) }}><label>Nota (opcional)<textarea name="note" maxLength={2000} rows={3} disabled={busy} /></label><p>Marca para reescribir, sin cambios automáticos.</p><button className="primary" disabled={busy}>Marcar para cambiar ⚑</button><button type="button" disabled={busy} onClick={() => setFlagging(false)}>Volver a alternativas</button></form> : <>
          <p>Elige una alternativa para aplicar y aprobar. Variantes: solo lectura; no cambian la palabra.</p><div className="field-tabs" role="group" aria-label="Campo a corregir">{(Object.keys(labels) as AlternativeField[]).map(key => <button key={key} disabled={busy} aria-pressed={field === key} onClick={() => setField(key)}>{labels[key]}</button>)}</div>
          {(field === 'gloss' || field === 'example') && <label>Acepción<select value={sense} onChange={e => setSense(Number(e.target.value))}>{selected.senses.map(s => <option key={s.order} value={s.order}>{s.order + 1}: {s.gloss}</option>)}</select></label>}
          <p className="original-value"><strong>Original: </strong>{field === 'lemma' ? selected.headword : field === 'gloss' ? selected.senses.find(s => s.order === sense)?.gloss : field === 'example' ? selected.senses.find(s => s.order === sense)?.exampleEs : field === 'regional_variant' ? selected.regionNotes ?? 'Sin nota regional' : selected.registerNotes ?? 'Sin nota de registro'}</p>
          <div className="alternative-list">{selected.alternatives.filter(a => a.field === field).map(a => {
            const content = <><strong lang={field === 'gloss' ? 'en' : 'es'}>{a.text}</strong>{a.translation && <span lang="en">{a.translation}</span>}<small>{a.reason}{a.support && ` · Apoyo: ${a.support}`}</small></>
            return field === 'regional_variant' || field === 'register_variant' ? <div className="readonly-variant" key={a.id}>{content}<span className="chip">Solo lectura · no cambia la palabra</span></div> : <button key={a.id} disabled={busy} onClick={() => void resolve(selected, { action: 'approve_alt', field, alternativeId: a.id, ...((field === 'gloss' || field === 'example') ? { senseOrder: sense } : {}) })}>{content}<small>Elegir y aprobar ✓</small></button>
          })}</div>{!selected.alternatives.some(a => a.field === field) && <p>Sin alternativas. Puedes editar o marcar para cambiar.</p>}
          <div className="drawer-footer"><button disabled={busy} onClick={() => setFlagging(true)}>Ninguna vale… Cambiar ⚑</button><button className="again" disabled={busy} onClick={() => void resolve(selected, { action: 'reject' })}>Rechazar palabra</button></div>
        </>}{rowErrors[selected.wordId] && <p className="review-failure" role="status">{rowErrors[selected.wordId]}</p>}</>}
    </dialog>
  </section>
}
