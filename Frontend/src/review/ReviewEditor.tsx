import { useEffect, useRef, useState } from 'react'
import type { ReviewEdit, ReviewItem } from '../api/review'
import { changedEdits, editValues } from './reviewModel'

const labels: Record<string, string> = { lemma: 'Palabra', article: 'Artículo', gender: 'Género', regional_note: 'Nota regional', gloss: 'Traducción', example: 'Ejemplo ES', accepted: 'Respuestas aceptadas ES (separadas por comas)' }
export function ReviewEditor({ word, save, cancel, busy }: { word: ReviewItem; save(edits: ReviewEdit[]): void; cancel(): void; busy: boolean }) {
  const original = editValues(word)
  const [draft, setDraft] = useState(original)
  const first = useRef<HTMLInputElement>(null)
  useEffect(() => { first.current?.focus() }, [])
  const changes = changedEdits(original, draft)
  return <form className="inline-edit" onSubmit={e => { e.preventDefault(); if (changes.length > 0 && changes.length <= 10) save(changes) }}>
    <h4>Editar y aprobar</h4><p className="secondary-text">Solo campos admitidos por la API. Ejemplo EN, notas generales y de registro: solo lectura.</p>
    <fieldset disabled={busy}>{draft.map((edit, i) => <label key={`${edit.field}-${edit.senseOrder ?? 'word'}`}>
      {labels[edit.field]}{edit.senseOrder !== undefined && ` · acepción ${edit.senseOrder + 1}`}
      <del id={`original-${word.wordId}-${i}`}>{original[i].after || '(vacío)'}</del>
      {edit.field === 'article' || edit.field === 'gender' ? <select aria-describedby={`original-${word.wordId}-${i}`} value={edit.after} onChange={e => setDraft(draft.map((d, n) => n === i ? { ...d, after: e.target.value } : d))}>
        {!original[i].after && <option value="">Sin definir (sin cambios)</option>}
        {(edit.field === 'article' ? ['el', 'la', 'los', 'las'] : ['masculine', 'feminine', 'common', 'not_applicable']).map(v => <option key={v} value={v}>{({ masculine: 'Masculino', feminine: 'Femenino', common: 'Común', not_applicable: 'No aplicable' } as Record<string, string>)[v] ?? v}</option>)}
      </select> : <input ref={i === 0 ? first : undefined} aria-describedby={`original-${word.wordId}-${i}`} required={['lemma', 'gloss', 'example', 'accepted'].includes(edit.field)} lang={edit.field === 'gloss' ? 'en' : 'es'} value={edit.after} onChange={e => setDraft(draft.map((d, n) => n === i ? { ...d, after: e.target.value } : d))} />}
    </label>)}</fieldset>
    {changes.length > 10 && <p role="status">Máximo 10 campos por aprobación. Reduce los cambios.</p>}
    <div className="edit-buttons"><button className="primary" type="submit" disabled={busy || !changes.length || changes.length > 10}>Guardar y aprobar ✓</button><button type="button" disabled={busy} onClick={cancel}>Cancelar</button></div>
  </form>
}
