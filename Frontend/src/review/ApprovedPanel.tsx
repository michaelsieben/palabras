import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { ApiError } from '../api/client'
import { getApprovedWords, getReviewQueue } from '../api/review'
import type { ApprovedWord, ReviewItem } from '../api/review'
import type { getReviewQueueStore, ReviewQueuedRow } from './reviewQueue'
import { ReviewRowStatus } from './ReviewRowStatus'

type Word = ApprovedWord | ReviewItem
const panelRow = (r: ReviewQueuedRow) => r.action.action === 'demote' || 'gloss' in r.word || r.word.status === 'approved' || r.word.status === 'rejected'
async function loadWords(scope: 'approved' | 'rejected', search: string, after?: string, signal?: AbortSignal): Promise<{ items: Word[]; nextCursor?: string }> {
  // Preserve global approved search; withdrawn discovery uses the explicit rejected queue scope.
  if (scope === 'approved') return getApprovedWords(search, after, signal)
  const result = await getReviewQueue(signal, 'rejected', after)
  return { items: result.items, nextCursor: result.nextAfter ?? undefined }
}
export function ApprovedPanel({ queue }: { queue: ReturnType<typeof getReviewQueueStore> }) {
  const snapshot = useSyncExternalStore(queue.subscribe, queue.getSnapshot)
  const [scope, setScope] = useState<'approved' | 'rejected'>('approved')
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  const [items, setItems] = useState<Word[]>([])
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  const [next, setNext] = useState<string>()
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState<number>()
  const [rowErrors, setRowErrors] = useState<Record<number, string>>({})
  const requestId = useRef(0)
  const seenStale = useRef(new Set<string>())
  const seenCursorRestarts = useRef(new Set<string>())
  const mutationBusy = useRef(false)
  const after = cursors.at(-1)
  const scopeLabel = scope === 'approved' ? 'aprobadas' : 'retiradas'
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const id = ++requestId.current
    try {
      await queue.flush()
      if (id !== requestId.current || signal?.aborted) return
      setLoading(true)
      const acknowledged = queue.getSnapshot().rows.filter(r => r.state === 'applied' && panelRow(r))
      const result = await loadWords(scope, search, after, signal)
      if (id !== requestId.current || signal?.aborted) return
      setItems(result.items); setNext(result.nextCursor); setError('')
      for (const row of acknowledged) await queue.discard(row.action.clientReviewId)
    } catch (failure) {
      if (id !== requestId.current || signal?.aborted) return
      if (failure instanceof ApiError && failure.status === 400 && after) {
        const cursorKey = JSON.stringify([scope, search, after])
        if (!seenCursorRestarts.current.has(cursorKey)) {
          seenCursorRestarts.current.add(cursorKey)
          // Invalidate concurrent refreshes; stale-version renew uses the same request generation.
          ++requestId.current
          setCursors([undefined]); setNext(undefined); setError(''); setLoading(true)
          setNotice('La lista ha cambiado. Volvemos a la página 1.')
          return
        }
      }
      setError(`No se han podido cargar las ${scopeLabel}. Conéctate y vuelve a intentarlo.`)
    } finally { if (id === requestId.current && !signal?.aborted) setLoading(false) }
  }, [after, search, scope, scopeLabel, queue])
  useEffect(() => {
    const controller = new AbortController()
    queueMicrotask(() => { if (!controller.signal.aborted) void refresh(controller.signal) })
    return () => controller.abort()
  }, [refresh])
  async function decide(word: Word, action: 'demote' | 'reject' | 'approve') {
    if (mutationBusy.current) return
    mutationBusy.current = true
    setBusy(word.wordId)
    try {
      await queue.enqueue({ clientReviewId: crypto.randomUUID(), wordId: word.wordId, wordVersion: word.version, action }, { ...word, status: word.status ?? 'approved' })
      setRowErrors(e => ({ ...e, [word.wordId]: '' })); setNotice(`${word.headword}: decisión guardada aquí; pendiente de confirmación.`)
    }
    catch { setRowErrors(e => ({ ...e, [word.wordId]: 'No se ha guardado la decisión. Reintenta; no cierres esta vista.' })) }
    finally { mutationBusy.current = false; setBusy(undefined) }
  }
  const renew = useCallback(async (decisions: { id: string; wordId: number }[], signal?: AbortSignal) => {
    // Return to first page; preserve failed decision until a fresh version is actually fetched.
    const request = ++requestId.current
    try {
      const result = await loadWords(scope, search, undefined, signal)
      if (request !== requestId.current || signal?.aborted) return
      const wasStale = queue.getSnapshot().rows.some(r => r.result === 'stale_version' && decisions.some(d => d.id === r.action.clientReviewId))
      setItems(result.items); setNext(result.nextCursor); setCursors(c => c.length === 1 ? c : [undefined]); setError(''); setLoading(false); setNotice(`${wasStale ? 'La palabra ha cambiado. ' : ''}Palabras actualizadas desde la página 1. Revisa el estado y el contenido antes de decidir otra vez.`)
      for (const { id, wordId } of decisions) {
        if (result.items.some(w => w.wordId === wordId)) {
          const stale = queue.getSnapshot().rows.some(r => r.action.clientReviewId === id && r.result === 'stale_version')
          await queue.discard(id)
          setRowErrors(e => ({ ...e, [wordId]: stale ? 'La palabra ha cambiado. Contenido actualizado; revísalo antes de decidir otra vez.' : '' }))
        }
        else setNotice('Palabra no encontrada en esta página. La decisión local sigue disponible; comprueba las aprobadas y las retiradas o continúa a la siguiente página.')
      }
    } catch {
      if (request !== requestId.current || signal?.aborted) return
      setLoading(false)
      for (const { wordId } of decisions) setRowErrors(e => ({ ...e, [wordId]: 'No se pudo actualizar esta palabra. La decisión sigue guardada.' }))
    }
  }, [search, scope, queue])
  const staleDecisions = JSON.stringify(snapshot.rows.filter(r => panelRow(r) && r.state === 'failed' && r.result === 'stale_version').map(r => ({ id: r.action.clientReviewId, wordId: r.action.wordId })))
  useEffect(() => {
    const stale: { id: string; wordId: number }[] = JSON.parse(staleDecisions)
    const newlyStale = stale.filter(r => !seenStale.current.has(r.id))
    const controller = new AbortController()
    queueMicrotask(() => {
      if (controller.signal.aborted) return
      seenStale.current = new Set(stale.map(r => r.id))
      if (newlyStale.length) void renew(newlyStale, controller.signal)
    })
    return () => controller.abort()
  }, [staleDecisions, renew])
  const rows = snapshot.rows.filter(panelRow)
  const rowByWord = new Map(rows.map(r => [r.word.wordId, r]))
  const visible = items
  // Failed/offline decisions remain actionable across filters, pagination and reloads.
  const detached = rows.filter(r => !items.some(w => w.wordId === r.word.wordId))
  return <section className="review-section" aria-label="Palabras aprobadas y retiradas">
    <div className="field-tabs" role="group" aria-label="Estado de las palabras">{(['approved', 'rejected'] as const).map(value => <button key={value} aria-pressed={scope === value} disabled={busy !== undefined} onClick={() => { if (value === scope) return; ++requestId.current; setScope(value); setItems([]); setNext(undefined); setCursors([undefined]); setLoading(true); setError(''); setNotice('') }}>{value === 'approved' ? 'Aprobadas' : 'Retiradas'}</button>)}</div>
    {scope === 'approved' && <form className="review-search" onSubmit={e => { e.preventDefault(); setSearch(query.trim()); setCursors([undefined]) }}><label>Buscar aprobadas<input type="search" value={query} onChange={e => setQuery(e.target.value)} /></label><button type="submit">Buscar</button></form>}
    <p className="note">{scope === 'approved' ? 'Retirar: la palabra dejará de aparecer en el aprendizaje. Se pausarán sus tarjetas; el historial se conservará.' : 'Volver a incluir: las tarjetas pausadas por la retirada volverán como nuevas. El historial se conservará.'}</p>
    <p role="status">{loading ? `Cargando ${scopeLabel}…` : `Página ${cursors.length}`}{notice && ` · ${notice}`}</p>
    {snapshot.storageError && <p className="note" role="status">Almacenamiento sin confirmar. Conserva esta vista y reintenta.</p>}
    {error && <div className="note" role="status"><p>{error}</p><button onClick={() => void refresh()}>Actualizar {scopeLabel}</button></div>}
    <div className="review-grid">{[...visible, ...detached.map(r => r.word)].map(word => {
      const row = rowByWord.get(word.wordId)
      const status = word.status ?? 'approved'
      return <article className="panel review-card" key={word.wordId} aria-label={`${word.article ?? ''} ${word.headword}`.trim()}>
        <p className="eyebrow">{word.topic ?? 'Sin tema'}</p><span className="chip">{status === 'approved' ? 'Aprobada' : status === 'rejected' ? 'Retirada' : 'Pendiente'}</span><h3>{word.article} {word.headword}</h3><p className="gloss" lang="en">{'gloss' in word ? word.gloss : word.senses.map(s => s.gloss).join(' · ')}</p>
        <p className="secondary-text">{'approvedAt' in word && `Aprobada el ${new Intl.DateTimeFormat('es-ES', { dateStyle: 'medium' }).format(new Date(word.approvedAt))} · `}versión {word.version}{row && ' · estado leído antes de la decisión'}</p>
        {row ? <ReviewRowStatus row={row} confirmedRefresh retry={() => void queue.retry(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [word.wordId]: 'No se pudo reintentar.' })))} refresh={() => row.state === 'applied' ? void refresh() : void renew([{ id: row.action.clientReviewId, wordId: word.wordId }])} discard={() => void queue.discard(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [word.wordId]: 'No se pudo descartar.' })))} /> : <div className="review-actions">{status === 'approved' ? <><button className="again" disabled={busy !== undefined || loading} onClick={() => void decide(word, 'reject')}>Retirar</button><button disabled={busy !== undefined || loading} onClick={() => void decide(word, 'demote')}>Revisar otra vez</button></> : status === 'rejected' && <button className="primary" disabled={busy !== undefined || loading} onClick={() => void decide(word, 'approve')}>Volver a incluir</button>}</div>}
        {rowErrors[word.wordId] && <p className="review-failure" role="status">{rowErrors[word.wordId]}</p>}
      </article>
    })}</div>
    {!loading && !error && !visible.length && !detached.length && <p className="panel">{scope === 'approved' ? 'Sin resultados. Prueba otra búsqueda.' : 'No hay palabras retiradas.'}</p>}
    <div className="review-pagination"><button disabled={loading || cursors.length === 1} onClick={() => setCursors(c => c.slice(0, -1))}>Anterior</button><button disabled={loading || !next} onClick={() => setCursors(c => [...c, next])}>Siguiente</button><button disabled={loading} onClick={() => { if (after) setCursors([undefined]); else void refresh() }}>Actualizar {scopeLabel}</button></div>
  </section>
}
