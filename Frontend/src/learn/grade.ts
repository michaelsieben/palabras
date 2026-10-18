export type Article = 'el' | 'la' | 'los' | 'las'
export type GenderWord = 'masculine' | 'feminine' | 'masculine or feminine'

// Structural subset of the frozen PlanItem contract; no UI or API dependency.
export type GradeCard = {
  word: { headword: string; article: string | null; gender: string | null }
  sense: { acceptedEs: readonly string[]; gloss: string; accentsAreThePoint: boolean }
}

type Outcomes = {
  correct: [true, true, 3]
  'correct-but-slow': [true, true, 2]
  'near-miss-accent': [true, false, 3]
  'accent-required': [true, false, 1]
  'near-miss-case': [true, true, 3]
  'near-miss-punctuation': [true, true, 3]
  'missing-article': [true, false, 2]
  'wrong-gender': [true, false, 2]
  wrong: [false, false, 1]
  skipped: [false, null, 1]
  'self-override': [true, null, 3]
}

export type FeedbackKind = keyof Outcomes
type Details = {
  given: string; answer: string; gloss: string; headword: string
  article: string | null; genderWord: GenderWord | null
}
export type GradeResult = {
  [K in FeedbackKind]: Details & {
    kind: K; isCorrectMeaning: Outcomes[K][0]; isCorrectForm: Outcomes[K][1]
    suggestedRating: Outcomes[K][2]
  }
}[FeedbackKind]

const outcomes: { [K in FeedbackKind]: Outcomes[K] } = {
  correct: [true, true, 3], 'correct-but-slow': [true, true, 2],
  'near-miss-accent': [true, false, 3], 'accent-required': [true, false, 1],
  'near-miss-case': [true, true, 3], 'near-miss-punctuation': [true, true, 3],
  'missing-article': [true, false, 2], 'wrong-gender': [true, false, 2],
  wrong: [false, false, 1], skipped: [false, null, 1], 'self-override': [true, null, 3],
}
const punctuation = /[¿¡.,!?;:"']/gu
const spaces = (text: string) => text.normalize('NFC').trim().replace(/\s+/gu, ' ')
const lower = (text: string) => text.toLocaleLowerCase('es')
export const normalizeAnswer = (text: string) => lower(spaces(text).replace(punctuation, ''))

// Split around ñ before decomposition: its tilde must never be stripped.
export const foldAccents = (text: string) => text.normalize('NFC').split(/([ñÑ])/u)
  .map(part => /^[ñÑ]$/u.test(part) ? part : part.normalize('NFD').replace(/\p{M}/gu, '')).join('')

function details(card: GradeCard, given: string): Details {
  const { article, headword, gender } = card.word
  const genderWord = gender === 'common' ? 'masculine or feminine'
    : gender === 'masculine' || gender === 'feminine' ? gender : null
  return { given, answer: article ? `${article} ${headword}` : headword,
    gloss: card.sense.gloss, headword, article, genderWord }
}

function result(kind: FeedbackKind, data: Details): GradeResult {
  const [isCorrectMeaning, isCorrectForm, suggestedRating] = outcomes[kind]
  return { ...data, kind, isCorrectMeaning, isCorrectForm, suggestedRating } as GradeResult
}

export function gradeAnswer(given: string, card: GradeCard, options: { isSlow?: boolean } = {}): GradeResult {
  const data = details(card, given)
  if (!given.trim()) return result('skipped', data)
  const input = normalizeAnswer(given)
  // API defaults to bare headwords (or an empty list). Article-bearing cards require
  // the article even when an accepted literal omits it; canonical form is always valid.
  const accepted = [data.answer, ...card.sense.acceptedEs.map(answer =>
    data.article && !/^(el|la|los|las) /u.test(normalizeAnswer(answer))
      ? `${data.article} ${answer}` : answer)]
    .map(answer => ({ answer, normalized: normalizeAnswer(answer) }))
  const exact = accepted.find(candidate => candidate.normalized === input)
  if (exact) {
    // Inspect NFC/space-normalized originals, before destructive case/punctuation normalization.
    const raw = spaces(given)
    const target = spaces(exact.answer)
    const kind = raw !== target && lower(raw) === lower(target) ? 'near-miss-case'
      : raw !== target && raw.replace(punctuation, '') === target.replace(punctuation, '') ? 'near-miss-punctuation'
      : options.isSlow ? 'correct-but-slow'
      : 'correct'
    return result(kind, data)
  }
  const otherArticle: Record<Article, Article> = { el: 'la', la: 'el', los: 'las', las: 'los' }
  if (data.article && data.article in otherArticle) {
    const article = data.article as Article
    for (const candidate of accepted) {
      if (!candidate.normalized.startsWith(`${article} `)) continue
      const body = candidate.normalized.slice(article.length + 1)
      if (input === body) return result('missing-article', data)
      if (input === `${otherArticle[article]} ${body}`) return result('wrong-gender', data)
    }
  }
  const accent = accepted.find(candidate => foldAccents(candidate.normalized) === foldAccents(input))
  if (accent) return result(card.sense.accentsAreThePoint ? 'accent-required' : 'near-miss-accent',
    data)
  return result('wrong', data)
}

export function overrideGrade(given: string, card: GradeCard): GradeResult {
  return result('self-override', details(card, given))
}
