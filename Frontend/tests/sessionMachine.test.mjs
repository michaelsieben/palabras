import test from 'node:test'
import assert from 'node:assert/strict'
import { initialSessionState, isLearningInSession, sessionReducer, sessionTotals } from '../src/learn/sessionMachine.ts'

const item = (cardId, extra = {}) => ({
  cardId, cardEpoch: 0, direction: 'es_en', mode: 'recognition', isNew: false,
  word: { key: `word-${cardId}`, headword: 'café', article: null, gender: 'not_applicable', pos: 'noun' },
  sense: { gloss: 'coffee', exampleEs: 'Un café.', exampleEn: 'A coffee.', acceptedEs: ['café'], acceptedEn: ['coffee'], accentsAreThePoint: false },
  notes: {}, ...extra,
})
const plan = items => ({ sessionId: 'session', generatedAtUtc: '2026-10-18T12:00:00Z', localDate: '2026-10-18', items,
  counts: { due: items.length, newOffered: 0, backlog: 0 }, warnings: [] })
function start(items) { return sessionReducer(sessionReducer(initialSessionState, { type: 'load' }), { type: 'loaded', plan: plan(items) }) }
let attemptNumber = 0
const rate = (state, rating, extra = {}) => sessionReducer(state, { type: 'rate', rating,
  clientAttemptId: `attempt-${++attemptNumber}`, reviewedAtUtc: '2026-10-18T12:00:00Z', durationMs: 4000, ...extra })
function recognize(state, rating) {
  return sessionReducer(rate(sessionReducer(state, { type: 'reveal' }), rating), { type: 'next' })
}

test('idle/loading/empty/error/retry and another round are deterministic', () => {
  assert.equal(initialSessionState.phase, 'idle')
  assert.equal(start([]).phase, 'empty')
  const loading = sessionReducer(initialSessionState, { type: 'load' })
  assert.equal(loading.phase, 'loading')
  const error = sessionReducer(loading, { type: 'failed', message: 'network' })
  assert.equal(error.phase, 'error')
  assert.equal(sessionReducer(error, { type: 'load' }).phase, 'loading')
  const done = recognize(start([item(1)]), 3)
  assert.equal(done.phase, 'summary')
  assert.equal(sessionReducer(done, { type: 'load' }).attempts.length, 0)
})

test('recognition requires reveal, emits one real attempt, rejects double rate', () => {
  const state = start([item(1)])
  assert.equal(rate(state, 3), state)
  const grading = sessionReducer(state, { type: 'reveal' })
  assert.equal(grading.phase, 'grading')
  const next = rate(grading, 3)
  assert.equal(next.phase, 'next')
  assert.equal(rate(next, 3), next)
  assert.deepEqual(next.attempts[0], {
    clientAttemptId: next.attempts[0].clientAttemptId, cardId: 1, cardEpoch: 0, sessionId: 'session', mode: 'recognition', rating: 3,
    isCorrectMeaning: null, isCorrectForm: null, overridden: false, feedbackKind: null, answerText: null, durationMs: 4000, reviewedAtUtc: '2026-10-18T12:00:00Z',
  })
})

test('failed fetch with cached plan enters offline prompting and preserves pure requeue; empty cache enters empty', () => {
  const cached = plan([item(1, { mode: 'typed', direction: 'en_es' })])
  const before = structuredClone(cached)
  const loading = sessionReducer(initialSessionState, { type: 'load' })
  let state = sessionReducer(loading, { type: 'failed', message: 'network', cachedPlan: cached })
  assert.equal(state.phase, 'prompting'); assert.equal(state.offline, true)
  state = sessionReducer(state, { type: 'answer', value: 'cafe' })
  state = sessionReducer(state, { type: 'submit', durationMs: 1000 })
  state = sessionReducer(rate(state, 3), { type: 'next' })
  assert.equal(state.phase, 'summary'); assert.equal(state.attempts[0].sessionId, cached.sessionId)
  assert.deepEqual(cached, before)
  assert.equal(sessionReducer(loading, { type: 'failed', message: 'network', cachedPlan: plan([]) }).phase, 'empty')
  assert.equal(sessionReducer(state, { type: 'load' }).offline, false)
})

test('Again requeues three other items later; every repeat emits unique real attempt', () => {
  let state = recognize(start(Array.from({ length: 6 }, (_, i) => item(i + 1))), 1)
  assert.deepEqual(state.queue.map(card => card.cardId), [2, 3, 4, 1, 5, 6])
  for (let i = 0; i < 3; i++) state = recognize(state, 3)
  assert.equal(state.queue[0].cardId, 1)
  state = recognize(state, 3)
  assert.equal(state.attempts.filter(attempt => attempt.cardId === 1).length, 2)
  assert.equal(new Set(state.attempts.map(attempt => attempt.clientAttemptId)).size, 5)
})

test('Hard requeues a new/locally learning card six later, not a review card', () => {
  const cards = Array.from({ length: 9 }, (_, i) => item(i + 1))
  let state = recognize(start([{ ...cards[0], isNew: true }, ...cards.slice(1)]), 2)
  assert.deepEqual(state.queue.map(card => card.cardId), [2, 3, 4, 5, 6, 7, 1, 8, 9])
  state = recognize(start(cards), 2)
  assert.equal(state.queue.some(card => card.cardId === 1), false)
})

test('learning interpretation uses latest Again/Good/Easy, never repetition count or unrelated cards', () => {
  for (const isNew of [false, true]) {
    const card = item(1, { isNew })
    const history = ratings => ratings.map(rating => ({ cardId: 1, rating }))
    assert.equal(isLearningInSession(card, []), isNew)
    assert.equal(isLearningInSession(card, history([2, 2])), isNew)
    assert.equal(isLearningInSession(card, history([1, 2])), true)
    for (const graduate of [3, 4]) {
      assert.equal(isLearningInSession(card, history([graduate])), false)
      assert.equal(isLearningInSession(card, history([1, graduate, 2])), false)
      assert.equal(isLearningInSession(card, history([graduate, 1, 2])), true)
    }
    assert.equal(isLearningInSession(card, [{ cardId: 2, rating: 1 }]), isNew)
  }
})

test('Hard keeps an Again card learning and requeues it six items later', () => {
  let state = recognize(start(Array.from({ length: 10 }, (_, i) => item(i + 1))), 1)
  for (let i = 0; i < 3; i++) state = recognize(state, 3)
  assert.equal(state.queue[0].cardId, 1)
  state = recognize(state, 2)
  assert.deepEqual(state.queue.map(card => card.cardId), [5, 6, 7, 8, 9, 10, 1])
  assert.equal(isLearningInSession(item(1), state.attempts), true)
  assert.equal(state.extraReps[1], 2)
  const repeats = state.attempts.filter(attempt => attempt.cardId === 1)
  assert.deepEqual(repeats.map(attempt => attempt.rating), [1, 2])
  assert.equal(new Set(repeats.map(attempt => attempt.clientAttemptId)).size, 2)
})

test('Good/Easy graduate both new and Again cards; later Hard does not requeue', () => {
  for (const isNew of [false, true]) for (const graduate of [3, 4]) {
    // Repeated plan entry exercises a later encounter with the same original isNew flag.
    // Existing extraReps remains nonzero after graduation and must not imply learning.
    const card = item(1, { isNew })
    let state = recognize(start([card, card, item(2)]), 1)
    state = recognize(state, graduate)
    assert.equal(isLearningInSession(card, state.attempts), false)
    assert.equal(state.extraReps[1], 1)
    assert.deepEqual(state.queue.map(value => value.cardId), [2, 1])
    state = recognize(state, 3)
    state = recognize(state, 2)
    assert.equal(state.phase, 'summary')
    assert.equal(state.queue.length, 0)
    assert.equal(state.extraReps[1], 1)
    assert.deepEqual(state.attempts.filter(attempt => attempt.cardId === 1).map(attempt => attempt.rating), [1, graduate, 2])
    state = recognize(start([card, card]), graduate)
    assert.equal(state.extraReps[1], undefined)
    assert.equal(isLearningInSession(card, state.attempts), false)
    state = recognize(state, 2)
    assert.equal(state.phase, 'summary')
    assert.equal(state.extraReps[1], undefined)
  }
})

test('Again re-enters learning after graduation and preserves three-item offset even at recovery cap', () => {
  for (const graduate of [3, 4]) {
    const card = item(1, { isNew: true })
    let state = recognize(start([card, card, ...Array.from({ length: 5 }, (_, i) => item(i + 2))]), graduate)
    state = recognize(state, 1)
    assert.equal(isLearningInSession(card, state.attempts), true)
    assert.deepEqual(state.queue.map(value => value.cardId), [2, 3, 4, 1, 5, 6])
  }
  let state = start(Array.from({ length: 12 }, (_, i) => item(i + 1)))
  for (let repetition = 0; repetition < 3; repetition++) {
    state = recognize(state, 1)
    assert.equal(state.queue[3].cardId, 1)
    assert.equal(state.queue.filter(card => card.cardId === 1).length, 1)
    if (repetition < 2) for (let i = 0; i < 3; i++) state = recognize(state, 3)
  }
  assert.equal(state.extraReps[1], 2)
  assert.notEqual(state.phase, 'summary')
})

test('normal Hard repeats cap at two; unresolved Again survives cap and cannot end on failure', () => {
  let state = start([item(1, { isNew: true })])
  for (let i = 0; i < 3; i++) state = recognize(state, 2)
  assert.equal(state.phase, 'summary')
  assert.equal(state.extraReps[1], 2)
  assert.equal(state.attempts.length, 3)
  assert.equal(new Set(state.attempts.map(attempt => attempt.clientAttemptId)).size, 3)
  state = start([item(1)])
  for (let i = 0; i < 5; i++) {
    state = recognize(state, 1)
    assert.equal(state.phase, 'prompting')
    assert.equal(state.queue.length, 1)
  }
  assert.equal(state.extraReps[1], 2)
  state = recognize(state, 3)
  assert.equal(state.phase, 'summary')
  assert.equal(state.attempts.length, 6)
  assert.deepEqual(sessionTotals(state.attempts), { total: 1, firstTry: 0, secondLook: 1 })
})

test('typed/cloze grading, slow suggestion, accent policy, Easy and self-override metadata', () => {
  for (const mode of ['typed', 'cloze']) {
    let state = start([item(1, { mode, direction: 'en_es' })])
    state = sessionReducer(state, { type: 'answer', value: 'cafe' })
    assert.equal(state.phase, 'answering')
    state = sessionReducer(state, { type: 'submit', durationMs: 2000 })
    assert.equal(state.feedback.kind, 'near-miss-accent')
    state = rate(state, 4)
    assert.equal(state.attempts[0].rating, 4)
    assert.equal(state.attempts[0].isCorrectForm, false)
    assert.equal(state.attempts[0].answerText, 'cafe')
    state = start([item(1, { mode })])
    state = sessionReducer(state, { type: 'answer', value: 'unexpected alternative' })
    state = sessionReducer(state, { type: 'submit', durationMs: 2000 })
    state = rate(state, undefined, { override: true })
    assert.deepEqual([state.attempts[0].rating, state.attempts[0].overridden, state.attempts[0].feedbackKind, state.attempts[0].isCorrectMeaning, state.attempts[0].isCorrectForm], [3, true, 'self-override', true, null])
  }
  let state = start([item(1, { mode: 'typed' })])
  state = sessionReducer(state, { type: 'answer', value: 'café' })
  state = sessionReducer(state, { type: 'submit', durationMs: 12001 })
  assert.equal(state.feedback.suggestedRating, 2)
  state = rate(state)
  assert.equal(state.attempts[0].rating, 2)
})

test('stop suggestion starts at six, below 60%, using only last eight; stop recovers failures', () => {
  let state = start(Array.from({ length: 20 }, (_, i) => item(i + 1)))
  for (let i = 0; i < 5; i++) state = recognize(state, 1)
  assert.equal(state.stopSuggested, false)
  state = recognize(state, 1)
  assert.equal(state.stopSuggested, true)
  state = sessionReducer(state, { type: 'stop' })
  assert.equal(state.stopping, true)
  assert.equal(state.phase, 'prompting')
  while (state.phase !== 'summary') state = recognize(state, 3)
  assert.equal(state.failed.length, 0)
  assert.notEqual(state.attempts.at(-1).rating, 1)
  state = start(Array.from({ length: 20 }, (_, i) => item(i + 1)))
  for (let i = 0; i < 8; i++) state = recognize(state, 3)
  for (let i = 0; i < 4; i++) state = recognize(state, 1)
  assert.equal(state.stopSuggested, true)
  for (let i = 0; i < 8; i++) state = recognize(state, 3)
  assert.equal(state.stopSuggested, false)
})

test('rating/answer cannot mutate frozen incoming plan; skip requeues and caps duration', () => {
  const original = plan([item(1, { mode: 'typed' })])
  const snapshot = structuredClone(original)
  let state = sessionReducer(sessionReducer(initialSessionState, { type: 'load' }), { type: 'loaded', plan: original })
  state = sessionReducer(state, { type: 'submit', durationMs: 900000 })
  state = rate(state, undefined, { durationMs: 900000 })
  assert.equal(state.attempts[0].feedbackKind, 'skipped')
  assert.equal(state.attempts[0].durationMs, 600000)
  assert.equal(state.queue[0].cardId, 1)
  assert.deepEqual(original, snapshot)
})
