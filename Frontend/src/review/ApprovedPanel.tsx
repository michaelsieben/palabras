import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { ApiError } from '../api/client'
import { getApprovedWords } from '../api/review'
import type { ApprovedWord } from '../api/review'
import type { getReviewQueueStore } from './reviewQueue'
import { ReviewRowStatus } from './ReviewRowStatus'

export function ApprovedPanel({ queue }: { queue: ReturnType<typeof getReviewQueueStore> }) {
  const snapshot = useSyncExternalStore(queue.subscribe, queue.getSnapshot)
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  const [items, setItems] = useState<ApprovedWord[]>([])
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  const [next, setNext] = useState<string>()
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState<number>()
  const [rowErrors, setRowErrors] = useState<Record<number, string>>({})
  const requestId = useRef(0)
  const after = cursors.at(-1)
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const id = ++requestId.current
    try {
      await queue.flush()
      if (id !== requestId.current || signal?.aborted) return
      setLoading(true)
      const acknowledged = queue.getSnapshot().rows.filter(r => r.state === 'applied' && r.action.action === 'demote')
      const result = await getApprovedWords(search, after, signal)
      if (id !== requestId.current || signal?.aborted) return
      setItems(result.items); setNext(result.nextCursor); setError('')
      for (const row of acknowledged) await queue.discard(row.action.clientReviewId)
    } catch (failure) {
      if (id !== requestId.current || signal?.aborted) return
      if (failure instanceof ApiError && failure.status === 400 && after) { setCursors([undefined]); setNotice('La lista ha cambiado. Volvemos a la página 1.'); return }
      setError('No se han podido cargar las aprobadas. Conéctate y vuelve a intentarlo.')
    } finally { if (id === requestId.current && !signal?.aborted) setLoading(false) }
  }, [after, search, queue])
  useEffect(() => {
    const controller = new AbortController()
    queueMicrotask(() => { if (!controller.signal.aborted) void refresh(controller.signal) })
    return () => controller.abort()
  }, [refresh])
  async function demote(word: ApprovedWord) {
    if (busy !== undefined) return
    setBusy(word.wordId)
    try { await queue.enqueue({ clientReviewId: crypto.randomUUID(), wordId: word.wordId, wordVersion: word.version, action: 'demote' }, word) }
    catch { setRowErrors(e => ({ ...e, [word.wordId]: 'No se ha guardado la decisión. Reintenta; no cierres esta vista.' })) }
    finally { setBusy(undefined) }
  }
  async function renew(id: string, wordId: number) {
    // Return to first page; preserve failed decision until a fresh version is actually fetched.
    try {
      const result = await getApprovedWords(search)
      const fresh = result.items.find(w => w.wordId === wordId)
      setItems(result.items); setNext(result.nextCursor); setCursors([undefined]); setNotice('Aprobadas actualizadas desde la página 1.')
      if (fresh) await queue.discard(id)
      else setNotice('Palabra no encontrada en esta página. La decisión local sigue disponible; busca su nombre para actualizarla.')
    } catch { setRowErrors(e => ({ ...e, [wordId]: 'No se pudo actualizar esta palabra. La decisión sigue guardada.' })) }
  }
  const rows = snapshot.rows.filter(r => r.action.action === 'demote')
  const rowByWord = new Map(rows.map(r => [r.word.wordId, r]))
  const visible = items
  // Failed/offline demotes remain actionable even when search/page no longer includes them.
  const detached = rows.filter(r => !items.some(w => w.wordId === r.word.wordId))
  return <section className="review-section" aria-label="Palabras aprobadas">
    <form className="review-search" onSubmit={e => { e.preventDefault(); setSearch(query.trim()); setCursors([undefined]) }}><label>Buscar aprobadas<input type="search" value={query} onChange={e => setQuery(e.target.value)} /></label><button type="submit">Buscar</button></form>
    <p role="status">{loading ? 'Cargando aprobadas…' : `Página ${cursors.length}`}{notice && ` · ${notice}`}</p>
    {error && <div className="note" role="status"><p>{error}</p><button onClick={() => void refresh()}>Actualizar aprobadas</button></div>}
    <div className="review-grid">{[...visible, ...detached.map(r => r.word as ApprovedWord)].map(word => {
      const row = rowByWord.get(word.wordId)
      return <article className="panel review-card" key={word.wordId} aria-label={`${word.article ?? ''} ${word.headword}`.trim()}>
        <p className="eyebrow">{word.topic ?? 'Sin tema'}</p><h3>{word.article} {word.headword}</h3><p className="gloss" lang="en">{word.gloss}</p>
        <p className="secondary-text">Aprobada el {new Intl.DateTimeFormat('es-ES', { dateStyle: 'medium' }).format(new Date(word.approvedAt))} · versión {word.version}</p>
        {row ? <ReviewRowStatus row={row} retry={() => void queue.retry(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [word.wordId]: 'No se pudo reintentar.' })))} refresh={() => void renew(row.action.clientReviewId, word.wordId)} discard={() => void queue.discard(row.action.clientReviewId).catch(() => setRowErrors(e => ({ ...e, [word.wordId]: 'No se pudo descartar.' })))} /> : <button disabled={busy !== undefined || loading} onClick={() => void demote(word)}>Revisar otra vez</button>}
        {rowErrors[word.wordId] && <p className="review-failure" role="status">{rowErrors[word.wordId]}</p>}
      </article>
    })}</div>
    {!loading && !error && !visible.length && !detached.length && <p className="panel">Sin resultados. Prueba otra búsqueda.</p>}
    <div className="review-pagination"><button disabled={loading || cursors.length === 1} onClick={() => setCursors(c => c.slice(0, -1))}>Anterior</button><button disabled={loading || !next} onClick={() => setCursors(c => [...c, next])}>Siguiente</button><button disabled={loading} onClick={() => { if (after) setCursors([undefined]); else void refresh() }}>Actualizar aprobadas</button></div>
  </section>
}
