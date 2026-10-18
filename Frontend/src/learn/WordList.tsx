import { useEffect, useState } from 'react'
import { getLearnerWords } from '../api/learn'
import type { LearnerWord } from '../api/learn'

export function WordList({ side = false }: { side?: boolean }) {
  const [search, setSearch] = useState('')
  const [topic, setTopic] = useState('')
  const [topics, setTopics] = useState<string[]>([])
  const [words, setWords] = useState<LearnerWord[]>([])
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading')
  const [retry, setRetry] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      setStatus('loading')
      void getLearnerWords(search, topic, controller.signal).then(items => {
        if (controller.signal.aborted) return
        setWords(items)
        if (!search && !topic) setTopics([...new Set(items.flatMap(word => word.topics))].sort())
        setStatus('ready')
      }).catch(() => { if (!controller.signal.aborted) setStatus('error') })
    }, 200)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [search, topic, retry])
  return <section className={`panel word-list ${side ? 'side-list' : ''}`} aria-labelledby={side ? 'side-words' : 'words-title'}>
    <p className="eyebrow">Solo palabras aprobadas</p><h2 id={side ? 'side-words' : 'words-title'}>Tus palabras</h2>
    <label>Buscar palabra<input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Español o inglés" /></label>
    <label>Tema<select value={topic} onChange={event => setTopic(event.target.value)}><option value="">Todas</option>{topics.map(value => <option key={value}>{value}</option>)}</select></label>
    {status === 'loading' ? <p role="status">Cargando palabras…</p> : status === 'error' ? <><p role="alert">No se han podido cargar las palabras.</p><button onClick={() => setRetry(value => value + 1)}>Reintentar</button></> : <>
      <ul>{words.map(word => <li key={word.key}><div><strong lang="es">{word.article} {word.headword}</strong><span lang="en">{word.gloss}</span></div><span className="chip">{word.stage}</span></li>)}</ul>
      {!words.length && <p role="status">No hay palabras aprobadas con este filtro.</p>}
    </>}
  </section>
}
