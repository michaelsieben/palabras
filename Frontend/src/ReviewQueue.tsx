import { useEffect, useRef, useState } from 'react'
import { approvedWords, reviewWords } from './mock/demo'
import type { AlternativeField, DemoWord } from './mock/demo'

const fieldLabels: Record<AlternativeField, string> = { gloss: 'Traducción', example: 'Ejemplo', variant: 'Variante', lemma: 'Palabra' }
const textFields = [['lemma', 'Palabra'], ['gloss', 'Traducción'], ['accepted', 'Otras respuestas aceptadas'], ['example', 'Ejemplo ES'], ['exampleEn', 'Ejemplo EN'], ['note', 'Nota']] as const

function EditableCard({ word, save, cancel }: { word: DemoWord; save(word: DemoWord): void; cancel(): void }) {
  const [draft, setDraft] = useState(word)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => { input.current?.focus() }, [])
  return <form className="inline-edit" onSubmit={e => { e.preventDefault(); save(draft) }}>
    <h3>Editar y aprobar</h3><p>Original conservado aquí mientras editas. Guardado solo en esta vista de ejemplo.</p>
    {textFields.map(([field, label], i) => <label key={field}>{label}<del id={`original-${word.id}-${field}`}>{word[field] || '(vacío)'}</del><input aria-label={label} aria-describedby={`original-${word.id}-${field}`} ref={i === 0 ? input : undefined} required={['lemma', 'gloss', 'example', 'exampleEn'].includes(field)} value={draft[field]} onChange={e => setDraft({ ...draft, [field]: e.target.value })} lang={field === 'gloss' || field === 'exampleEn' ? 'en' : 'es'} /></label>)}
    <div className="edit-pickers"><label>Artículo<select value={draft.article} onChange={e => setDraft({ ...draft, article: e.target.value })}>{['', 'el', 'la', 'los', 'las'].map(item => <option key={item} value={item}>{item || 'Sin artículo'}</option>)}</select></label><label>Género<select value={draft.gender} onChange={e => setDraft({ ...draft, gender: e.target.value })}><option value="m">Masculino</option><option value="f">Femenino</option><option value="mf">Común</option></select></label></div>
    <div className="edit-buttons"><button className="primary" type="submit">Guardar y aprobar ✓</button><button type="button" onClick={cancel}>Cancelar</button></div>
  </form>
}

export function ReviewQueue({ approved, onComplete }: { approved: boolean; onComplete(): void }) {
  const [queue, setQueue] = useState(reviewWords)
  const [accepted, setAccepted] = useState<DemoWord[]>([])
  const [editing, setEditing] = useState<string>()
  const [selected, setSelected] = useState<DemoWord>()
  const [field, setField] = useState<AlternativeField>('gloss')
  const [message, setMessage] = useState('')
  const [search, setSearch] = useState('')
  const [flagging, setFlagging] = useState(false)
  const dialog = useRef<HTMLDialogElement>(null)
  const opener = useRef<HTMLElement | null>(null)
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => {
    if (selected) dialog.current?.showModal()
    else dialog.current?.close()
  }, [selected])
  function dismiss() { setSelected(undefined); setFlagging(false); requestAnimationFrame(() => opener.current?.focus()) }
  function resolve(word: DemoWord, action: string, keep = false) {
    setQueue(items => items.filter(item => item.id !== word.id))
    if (keep) setAccepted(items => [...items, { ...word, status: 'approved' }])
    setMessage(`${word.article} ${word.lemma}: ${action}. Solo demostración; no guardado.`)
    setSelected(undefined); setEditing(undefined); setFlagging(false)
    onComplete()
    requestAnimationFrame(() => heading.current?.focus())
  }
  const items = approved ? [...approvedWords, ...accepted].filter(word => `${word.lemma} ${word.gloss}`.toLowerCase().includes(search.toLowerCase())) : queue
  return <section aria-labelledby="queue-heading">
    <div className="queue-heading"><div><h2 id="queue-heading" ref={heading} tabIndex={-1}>{approved ? 'Tu selección' : `${queue.length} pendientes`}</h2><p>{approved ? 'Palabras aprobadas de ejemplo.' : 'Revisa a tu ritmo. Tu español tiene la última palabra.'}</p></div><span className="chip">Sesión corta · hasta 15</span></div>
    <p className="review-status" role="status">{message}</p>
    {approved && <label className="review-search">Buscar aprobadas<input type="search" value={search} onChange={e => setSearch(e.target.value)} /></label>}
    <div className="review-grid">{items.map(word => <article className="panel review-card" key={word.id} aria-label={`${word.article} ${word.lemma}`}>
      <div className="card-top"><span className="eyebrow">{word.topic} · A2</span><span className="chip">{word.agreement} concordancia</span></div>
      <h3>{word.article} {word.lemma} <small>({word.gender})</small></h3>
      {editing === word.id ? <EditableCard word={word} save={draft => resolve(draft, 'editada y aprobada ✓', true)} cancel={() => { setEditing(undefined); requestAnimationFrame(() => document.getElementById(`edit-${word.id}`)?.focus()) }} /> : <>
        <p className="gloss" lang="en">{word.gloss}</p><p>{word.example}</p><p className="secondary-text" lang="en">{word.exampleEn}</p>{word.note && <p className="note">{word.note}</p>}
        {approved ? <button onClick={() => { setQueue(list => [...list, { ...word, status: 'pending_review' }]); setMessage(`${word.lemma}: revisión solicitada (solo ejemplo).`) }} disabled={queue.some(item => item.id === word.id)}>Revisar otra vez</button> : <div className="review-actions">
          <button className="primary" onClick={() => resolve(word, 'aprobada ✓', true)}>Vale ✓</button>
          <button className="again" onClick={e => { opener.current = e.currentTarget; setField('gloss'); setSelected(word) }}>No ✗</button>
          <button id={`edit-${word.id}`} onClick={() => setEditing(word.id)}>Editar ✎</button>
        </div>}
      </>}
    </article>)}</div>
    {!items.length && <div className="panel"><h3>{approved ? 'Sin resultados' : 'Listo por hoy'}</h3><p>{approved ? 'Prueba otra búsqueda.' : 'Bien hecho. Esta cola de ejemplo está resuelta.'}</p>{!approved && <button onClick={() => { setQueue(reviewWords); setMessage('Nueva vuelta de demostración.'); requestAnimationFrame(() => heading.current?.focus()) }}>¿Otra vuelta?</button>}</div>}
    <dialog className="alternatives-dialog" ref={dialog} aria-labelledby="alternatives-title" onCancel={e => { e.preventDefault(); dismiss() }}>
      {selected && <><div className="drawer-heading"><div><p className="eyebrow">{selected.article} {selected.lemma}</p><h2 id="alternatives-title">{flagging ? 'Cambiar ⚑' : '¿Qué encaja mejor?'}</h2></div><button autoFocus onClick={dismiss} aria-label="Cerrar alternativas">✕</button></div>
        {flagging ? <form onSubmit={e => { e.preventDefault(); resolve(selected, 'marcada para cambiar ⚑') }}><fieldset><legend>¿Qué hay que cambiar?</legend>{Object.entries(fieldLabels).map(([key, label]) => <label className="check-label" key={key}><input type="checkbox" name="fields" value={key} defaultChecked={key === field} />{label}</label>)}</fieldset><label>Nota (opcional)<textarea name="note" rows={3} /></label><p>Sin cambios automáticos. Nota solo de ejemplo.</p><button className="primary" type="submit">Marcar para cambiar ⚑</button><button type="button" onClick={() => setFlagging(false)}>Volver a alternativas</button></form> : <>
          <p>Alternativas ya preparadas. Toca una para aplicarla y aprobar.</p>
          <div className="field-tabs" role="group" aria-label="Campo a corregir">{Object.entries(fieldLabels).map(([key, label]) => <button key={key} aria-pressed={field === key} onClick={() => setField(key as AlternativeField)}>{label}</button>)}</div>
          <p className="original-value"><strong>Original: </strong>{field === 'gloss' ? selected.gloss : field === 'example' ? selected.example : field === 'lemma' ? selected.lemma : selected.note || 'Sin nota'}</p>
          <div className="alternative-list">{selected.alternatives.filter(alt => alt.field === field).map((alt, i) => <button key={i} onClick={() => {
            const next = { ...selected }
            if (field === 'gloss') next.gloss = alt.text
            else if (field === 'example') { next.example = alt.text; next.exampleEn = alt.translation ?? next.exampleEn }
            else if (field === 'lemma') next.lemma = alt.text
            else next.note = alt.text
            resolve(next, 'alternativa aplicada y aprobada ✓', true)
          }}><strong lang={field === 'gloss' ? 'en' : 'es'}>{alt.text}</strong>{alt.translation && <span lang="en">{alt.translation}</span>}<small>Concordancia {alt.support}/4 · elegir y aprobar ✓</small></button>)}</div>
          {!selected.alternatives.some(alt => alt.field === field) && <p>No hay alternativas para este campo. Puedes editarlo o marcarlo para cambiar.</p>}
          <div className="drawer-footer"><button onClick={() => setFlagging(true)}>Ninguna vale… Cambiar ⚑</button><button className="again" onClick={() => resolve(selected, 'rechazada ✗')}>Rechazar palabra</button></div>
        </>}
      </>}
    </dialog>
  </section>
}
