import type { Rating } from '../api/learn'
import { englishExplanations, spanishChrome } from '../copy/feedback'

const grades = [
  { rating: 1, name: 'Again', hint: 'No la recordaba' }, { rating: 2, name: 'Hard', hint: 'Con esfuerzo' },
  { rating: 3, name: 'Good', hint: 'La sabía' }, { rating: 4, name: 'Easy', hint: 'Sin dudar' },
] as const
export function GradeButtons({ onRate, slow }: { onRate(rating: Rating): void; slow: boolean }) {
  return <div className="session-actions"><p className="grading-hint">¿Cómo te ha ido? Tú eliges.{slow && ' Si costó recordarla, considera Hard.'}</p>
    <div className="grading-row">{grades.map(grade => <button key={grade.rating} className={grade.rating === 3 ? 'primary' : ''} onClick={() => onRate(grade.rating)} aria-describedby={grade.rating === 1 ? 'forgot-hint' : undefined}>
      <strong lang="en">{grade.name}</strong><span>{grade.hint}</span></button>)}</div>
    <p className="grading-hint" id="forgot-hint"><span lang="es">{spanishChrome['forgot-hint']} </span><span lang="en">{englishExplanations['forgot-hint']}</span></p>
  </div>
}
