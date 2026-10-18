import type { Attempt, PlanItem, Rating, SessionPlan } from '../api/learn.ts'
import { gradeAnswer, overrideGrade } from './grade.ts'
import type { GradeResult } from './grade.ts'
import { isSlowAnswer } from '../copy/feedback.ts'

export type SessionPhase = 'idle' | 'loading' | 'prompting' | 'answering' | 'grading' | 'feedback' | 'next' | 'summary' | 'empty' | 'error'
export type SessionState = {
  phase: SessionPhase; plan: SessionPlan | null; queue: PlanItem[]; answer: string
  feedback: GradeResult | null; attempts: Attempt[]; extraReps: Record<number, number>
  failed: PlanItem[]; stopSuggested: boolean; stopping: boolean; error: string | null
}
export const initialSessionState: SessionState = {
  phase: 'idle', plan: null, queue: [], answer: '', feedback: null, attempts: [],
  extraReps: {}, failed: [], stopSuggested: false, stopping: false, error: null,
}
export type SessionAction =
  | { type: 'load' }
  | { type: 'loaded'; plan: SessionPlan }
  | { type: 'failed'; message: string }
  | { type: 'answer'; value: string }
  | { type: 'reveal' }
  | { type: 'submit'; durationMs: number }
  | { type: 'rate'; rating?: Rating; override?: boolean; clientAttemptId: string; reviewedAtUtc: string; durationMs: number }
  | { type: 'next' }
  | { type: 'stop' }

// §5 interpretation approved by orchestrator: learning eligibility is local, not FSRS state.
// New cards start learning; the latest Again enters/re-enters learning, while Good/Easy
// graduate even an isNew card. Hard leaves eligibility unchanged. A future PlanItem.state
// can replace this one function if the owner wants server-decided eligibility.
export function isLearningInSession(card: Pick<PlanItem, 'cardId' | 'isNew'>,
  attempts: readonly Pick<Attempt, 'cardId' | 'rating'>[]): boolean {
  for (let index = attempts.length - 1; index >= 0; index--) {
    const attempt = attempts[index]
    if (attempt.cardId !== card.cardId || attempt.rating === 2) continue
    return attempt.rating === 1
  }
  return card.isNew
}

export function sessionReducer(state: SessionState, action: SessionAction): SessionState {
  if (action.type === 'load') return { ...initialSessionState, phase: 'loading' }
  if (action.type === 'loaded' && state.phase === 'loading') return {
    ...state, plan: action.plan, queue: [...action.plan.items], phase: action.plan.items.length ? 'prompting' : 'empty',
  }
  if (action.type === 'failed' && state.phase === 'loading') return { ...state, phase: 'error', error: action.message }
  const card = state.queue[0]
  switch (action.type) {
    case 'answer':
      return card && card.mode !== 'recognition' && ['prompting', 'answering'].includes(state.phase)
        ? { ...state, phase: 'answering', answer: action.value } : state
    case 'reveal':
      return card?.mode === 'recognition' && state.phase === 'prompting' ? { ...state, phase: 'grading' } : state
    case 'submit':
      return card && card.mode !== 'recognition' && ['prompting', 'answering'].includes(state.phase)
        ? { ...state, phase: 'feedback', feedback: gradeAnswer(state.answer, card, { isSlow: isSlowAnswer(action.durationMs, card.mode) }) } : state
    case 'rate': {
      if (!card || !state.plan || !['grading', 'feedback'].includes(state.phase)) return state
      const feedback = action.override && state.feedback ? overrideGrade(state.answer, card) : state.feedback
      const rating = action.override ? 3 : action.rating ?? feedback?.suggestedRating
      if (!rating) return state
      const attempt: Attempt = {
        clientAttemptId: action.clientAttemptId, cardId: card.cardId, cardEpoch: card.cardEpoch,
        sessionId: state.plan.sessionId, mode: card.mode, rating,
        isCorrectMeaning: feedback?.isCorrectMeaning ?? null, isCorrectForm: feedback?.isCorrectForm ?? null,
        overridden: Boolean(action.override && feedback), feedbackKind: feedback?.kind ?? null,
        answerText: card.mode === 'recognition' ? null : state.answer,
        durationMs: Math.min(600_000, Math.max(0, Math.round(action.durationMs))), reviewedAtUtc: action.reviewedAtUtc,
      }
      const attempts = [...state.attempts, attempt]
      const success = rating !== 1 && (feedback?.isCorrectMeaning ?? true)
      const failed = state.failed.filter(item => item.cardId !== card.cardId)
      if (!success) failed.push(card)
      const queue = state.queue.slice(1)
      const repeats = state.extraReps[card.cardId] ?? 0
      const learning = isLearningInSession(card, state.attempts)
      const requeue = rating === 1 || (rating === 2 && learning)
      const extraReps = { ...state.extraReps }
      if (requeue && repeats < 2) {
        queue.splice(Math.min(rating === 1 ? 3 : 6, queue.length), 0, card)
        extraReps[card.cardId] = repeats + 1
      }
      // C3 beats the normal two-extra-rep cap: unresolved misses must be re-asked.
      // Keep one rescue copy per failed card, even after the cap or a stop request.
      for (const missed of failed) if (!queue.some(item => item.cardId === missed.cardId)) {
        // Again retains its three-item offset when mandatory recovery exceeds the normal cap.
        if (missed.cardId === card.cardId && rating === 1) queue.splice(Math.min(3, queue.length), 0, missed)
        else queue.push(missed)
      }
      const recent = attempts.slice(-8)
      const accuracy = recent.filter(item => item.rating !== 1 && item.isCorrectMeaning !== false).length / recent.length
      return { ...state, phase: 'next', queue, attempts, extraReps, failed, feedback,
        stopSuggested: attempts.length >= 6 && accuracy < 0.6 }
    }
    case 'next':
      if (state.phase !== 'next') return state
      return { ...state, phase: state.queue.length ? 'prompting' : 'summary', answer: '', feedback: null }
    case 'stop': {
      if (!state.stopSuggested || !['prompting', 'answering', 'grading', 'feedback'].includes(state.phase)) return state
      // Finish recovery cards first; a stop never turns a miss into a summary.
      const queue = state.failed.length ? [...state.failed] : []
      return { ...state, stopping: true, queue, phase: queue.length ? 'prompting' : 'summary', answer: '', feedback: null }
    }
    default: return state
  }
}

export function sessionTotals(attempts: readonly Attempt[]) {
  const first = new Map<number, Attempt>()
  for (const attempt of attempts) if (!first.has(attempt.cardId)) first.set(attempt.cardId, attempt)
  const firstTry = [...first.values()].filter(item => item.rating !== 1 && item.isCorrectMeaning !== false).length
  return { total: first.size, firstTry, secondLook: first.size - firstTry }
}
