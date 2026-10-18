import { useEffect, useRef } from 'react'

export function AccentRow({ insert }: { insert(char: string): void }) {
  return <div className="accent-row" role="group" aria-label="Letras españolas">{['á', 'é', 'í', 'ó', 'ú', 'ñ', 'ü'].map(char =>
    <button type="button" key={char} onMouseDown={event => event.preventDefault()} onClick={() => insert(char)} aria-label={`Insertar ${char}`}>{char}</button>)}</div>
}
export function TypedAnswer({ answer, onChange, onSubmit }: { answer: string; onChange(value: string): void; onSubmit(): void }) {
  const input = useRef<HTMLInputElement>(null)
  const caret = useRef<{ start: number; end: number } | null>(null)
  useEffect(() => { input.current?.focus() }, [])
  useEffect(() => {
    if (caret.current) { input.current?.setSelectionRange(caret.current.start, caret.current.end); caret.current = null }
  }, [answer])
  function insert(char: string) {
    const field = input.current
    if (!field) return
    const start = field.selectionStart ?? answer.length
    const end = field.selectionEnd ?? start
    caret.current = { start: start + char.length, end: start + char.length }
    onChange(answer.slice(0, start) + char + answer.slice(end))
    field.focus()
  }
  return <form className="typed-answer" onSubmit={event => { event.preventDefault(); onSubmit() }}>
    <label>Tu respuesta<input ref={input} value={answer} onChange={event => onChange(event.target.value)} lang="es" autoComplete="off" autoCorrect="off" autoCapitalize="none" spellCheck={false} /></label>
    <AccentRow insert={insert} />
    <div className="answer-buttons"><button className="primary" type="submit">Comprobar</button><button type="button" onClick={() => { onChange(''); onSubmit() }}>No la sé</button></div>
  </form>
}
