import { ApiError, apiRequest } from './client.ts'

export type StatsDay = { date: string; answers: number; hits: number; misses: number; newWords: number; answerMs: number }
export type RetentionSlice = { reviews: number; passed: number }
export type StatsResponse = {
  localDate: string; from: string; to: string
  totals: { answers: number; hits: number; misses: number; formSlips: number; overridden: number
    firstTry: number; secondLook: number; answerMs: number; activeDays: number; newWords: number }
  days: StatsDay[]
  byMode: { mode: string; answers: number; hits: number; misses: number; formSlips: number }[]
  retention: { target: number; all: RetentionSlice; esEn: RetentionSlice; enEs: RetentionSlice }
  words: { approved: number; stages: { nuevo: number; aprendiendo: number; reciente: number; consolidada: number; dominada: number }
    leechCards: number; suspendedCards: { manual: number; leech: number } }
  hardest: { key: string; article: string | null; headword: string; gloss: string; misses: number }[]
  quality: { lateRows: number; missingDurationRows: number }
}

const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
const count = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) >= 0
const counts = (value: unknown, keys: readonly string[]): value is Record<string, number> => object(value) && keys.every(key => count(value[key]))
export function isLocalDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/u.test(value)) return false
  const ms = Date.parse(`${value}T00:00:00Z`)
  return Number.isFinite(ms) && new Date(ms).toISOString().slice(0, 10) === value
}
const slice = (value: unknown) => counts(value, ['reviews', 'passed']) && value.passed <= value.reviews
const activity = (value: unknown, extra: string[]) => counts(value, ['answers', 'hits', 'misses', ...extra])
  && value.hits + value.misses === value.answers

export function parseStats(body: unknown): StatsResponse {
  if (!object(body) || !isLocalDate(body.localDate) || !isLocalDate(body.from) || !isLocalDate(body.to)
    || body.from > body.to || body.to > body.localDate
    || !activity(body.totals, ['formSlips', 'overridden', 'firstTry', 'secondLook', 'answerMs', 'activeDays', 'newWords'])
    || !Array.isArray(body.days) || !body.days.every(day => object(day) && isLocalDate(day.date) && activity(day, ['newWords', 'answerMs']))
    || !Array.isArray(body.byMode) || !body.byMode.every(mode => object(mode) && typeof mode.mode === 'string' && activity(mode, ['formSlips']))
    || !object(body.retention) || typeof body.retention.target !== 'number' || !Number.isFinite(body.retention.target)
    || body.retention.target < 0 || body.retention.target > 1 || !['all', 'esEn', 'enEs'].every(key => slice(body.retention && (body.retention as Record<string, unknown>)[key]))
    || !counts(body.words, ['approved', 'leechCards']) || !counts(body.words.stages, ['nuevo', 'aprendiendo', 'reciente', 'consolidada', 'dominada'])
    || !counts(body.words.suspendedCards, ['manual', 'leech'])
    || !Array.isArray(body.hardest) || body.hardest.length > 5 || !body.hardest.every(word => object(word)
      && ['key', 'headword', 'gloss'].every(key => typeof word[key] === 'string')
      && (word.article === null || typeof word.article === 'string') && count(word.misses))
    || !counts(body.quality, ['lateRows', 'missingDurationRows'])) throw new ApiError('invalid-response')
  const expectedDays = (Date.parse(body.to) - Date.parse(body.from)) / 86_400_000 + 1
  if (expectedDays > 366 || body.days.length !== expectedDays || !body.days.every((day, index) =>
    day.date === new Date(Date.parse(body.from as string) + index * 86_400_000).toISOString().slice(0, 10))) throw new ApiError('invalid-response')
  return body as StatsResponse
}

export async function getStats(options: { from?: string; to?: string } = {}, signal?: AbortSignal): Promise<StatsResponse> {
  const query = new URLSearchParams()
  if (options.from !== undefined) query.set('from', options.from)
  if (options.to !== undefined) query.set('to', options.to)
  return parseStats(await apiRequest(`/api/learn/stats${query.size ? `?${query}` : ''}`, { signal }))
}
