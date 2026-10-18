import type { PlanItem } from '../api/learn'
import { SpeakButton } from './SpeakButton'

export function PromptCard({ item, revealed }: { item: PlanItem; revealed: boolean }) {
  const answer = [item.word.article, item.word.headword].filter(Boolean).join(' ')
  const recognition = item.mode === 'recognition'
  const articleRequired = item.word.article && item.sense.acceptedEs.some(value => value.startsWith(`${item.word.article} `))
  const prompt = recognition ? answer : item.mode === 'cloze' ? item.sense.clozeEs ?? item.sense.gloss : item.sense.gloss
  return <div className="flashcard">
    <p className="eyebrow">{item.isNew ? 'Nueva' : 'Repaso'} · {recognition ? 'ES → EN' : item.mode === 'cloze' ? 'Completa la frase' : 'EN → ES'}</p>
    <h2 className="lemma" lang={recognition || item.mode === 'cloze' ? 'es' : 'en'}>{prompt}</h2>
    <p>{recognition ? '¿Qué significa en inglés?' : item.mode === 'cloze' ? 'Escribe la respuesta que falta.' : '¿Cómo se dice en español?'}{!recognition && articleRequired ? ' Incluye el artículo.' : ''}</p>
    <SpeakButton text={recognition || item.mode === 'cloze' ? prompt : '¿Cómo se dice en español?'} label={item.mode === 'typed' ? 'Escuchar la instrucción en español' : 'Escuchar el enunciado en español de España'} />
    {revealed && recognition && <div className="answer"><h3 lang="en">{item.sense.gloss}</h3><p lang="es">{item.sense.exampleEs}</p><p lang="en" className="secondary-text">{item.sense.exampleEn}</p>
      {item.notes.region && <p className="note">{item.notes.region}</p>}{item.notes.register && <p className="note">{item.notes.register}</p>}</div>}
  </div>
}
