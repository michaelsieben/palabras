import type { GradeResult } from './grade'
import type { PlanItem, Rating } from '../api/learn'
import { CopyText } from './CopyText'
import { SpeakButton } from './SpeakButton'

export function FeedbackPanel({ result, item, onRate }: { result: GradeResult; item: PlanItem; onRate(rating?: Rating, override?: boolean): void }) {
  const correct = result.isCorrectMeaning && result.suggestedRating !== 1
  return <section className="panel feedback-panel" aria-label="Resultado de tu respuesta">
    <div role="status"><CopyText kind={result.kind} values={{ answer: result.answer, given: result.given, gloss: result.gloss, headword: result.headword, article: result.article, genderWord: result.genderWord }} /></div>
    <p lang="es">{item.sense.exampleEs}</p><p lang="en" className="secondary-text">{item.sense.exampleEn}</p>
    {item.notes.region && <p className="note">{item.notes.region}</p>}{item.notes.register && <p className="note">{item.notes.register}</p>}
    <SpeakButton text={result.answer} />
    <div className="answer-buttons"><button className="primary" onClick={() => onRate()}>Continuar · <span lang="en">{result.suggestedRating === 1 ? 'Again' : result.suggestedRating === 2 ? 'Hard' : 'Good'}</span></button>
      {correct && <button onClick={() => onRate(4)} lang="en">Easy</button>}
      {!correct || !result.isCorrectForm ? <button onClick={() => onRate(3, true)}>Yo tenía razón <span lang="en">· I was right</span></button> : null}
    </div>
  </section>
}
