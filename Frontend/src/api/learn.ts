import { ApiError, apiRequest } from './client.ts'
import type { FeedbackKind } from '../learn/grade.ts'

export type Rating = 1 | 2 | 3 | 4
export type PlanItem = {
  cardId: number; cardEpoch: number; direction: 'es_en' | 'en_es'
  mode: 'recognition' | 'typed' | 'cloze'; isNew: boolean
  word: { key: string; headword: string; article: string | null; gender: string | null; pos: string }
  sense: { gloss: string; exampleEs: string; exampleEn: string; clozeEs?: string
    acceptedEs: string[]; acceptedEn: string[]; accentsAreThePoint: boolean }
  notes: { region?: string; register?: string }
}
export type SessionPlan = {
  sessionId: string; generatedAtUtc: string; localDate: string; items: PlanItem[]
  counts: { due: number; newOffered: number; backlog: number }; warnings: string[]; nextDueUtc?: string
}
export type Attempt = {
  clientAttemptId: string; cardId: number; cardEpoch: number; sessionId: string
  mode: 'recognition' | 'typed' | 'cloze'; rating: Rating
  isCorrectMeaning: boolean | null; isCorrectForm: boolean | null; overridden: boolean
  feedbackKind: FeedbackKind | null; answerText: string | null; durationMs: number; reviewedAtUtc: string
}
export type AttemptResult = {
  clientAttemptId: string; status: 'applied' | 'duplicate' | 'stale_epoch' | 'suspended' | 'out_of_order'
  card?: { id: number; state: number; dueUtc: string; intervalDays: number; leech: boolean }
}
export type LearnerWord = { key: string; article: string | null; headword: string; gloss: string; topics: string[]; stage: string }
export type DayStatus = {
  localDate: string; status: 'done' | 'skipped' | 'missed' | null; streak: number
  skipAvailable: boolean; skipNextAvailable: string | null
}
export type DayCompletion = { localDate: string; durationS?: number; plannedS?: number; newCount?: number; reviewCount?: number }
export type SkipResult = { kind: 'skipped'; day: DayStatus } | { kind: 'already_done' } | { kind: 'skip_unavailable'; nextAvailable: string }
export function isLocalDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const date = new Date(`${value}T12:00:00Z`)
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value
}
export function parseDayStatus(body: unknown): DayStatus {
  if (!object(body) || !isLocalDate(body.localDate) || ![null, 'done', 'skipped', 'missed'].includes(body.status as null | string)
    || !Number.isSafeInteger(body.streak) || Number(body.streak) < 0 || typeof body.skipAvailable !== 'boolean'
    || !(body.skipNextAvailable === null || isLocalDate(body.skipNextAvailable))) throw new ApiError('invalid-response')
  return body as DayStatus
}
export async function getDayStatus(signal?: AbortSignal): Promise<DayStatus> {
  return parseDayStatus(await apiRequest('/api/learn/day/status', { signal }))
}
export async function completeDay(completion: DayCompletion): Promise<DayStatus> {
  return parseDayStatus(await apiRequest('/api/learn/day/done', { method: 'POST', body: JSON.stringify(completion) }))
}
export async function skipDay(localDate: string): Promise<SkipResult> {
  try {
    return { kind: 'skipped', day: parseDayStatus(await apiRequest('/api/learn/day/skip', {
      method: 'POST', body: JSON.stringify({ localDate, source: 'app' }),
    })) }
  } catch (error) {
    if (error instanceof ApiError && error.status === 409 && object(error.body)) {
      if (error.body.reason === 'already_done') return { kind: 'already_done' }
      if (error.body.reason === 'skip_unavailable' && isLocalDate(error.body.nextAvailable)) {
        return { kind: 'skip_unavailable', nextAvailable: error.body.nextAvailable }
      }
    }
    throw error
  }
}

const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string')
function planItem(value: unknown): value is PlanItem {
  if (!object(value) || !object(value.word) || !object(value.sense) || !object(value.notes)) return false
  const { word, sense, notes } = value
  return Number.isSafeInteger(value.cardId) && Number.isSafeInteger(value.cardEpoch)
    && ['es_en', 'en_es'].includes(String(value.direction)) && ['recognition', 'typed', 'cloze'].includes(String(value.mode))
    && typeof value.isNew === 'boolean' && ['key', 'headword', 'pos'].every(key => typeof word[key] === 'string')
    && (word.article === null || typeof word.article === 'string') && (word.gender === null || typeof word.gender === 'string')
    && ['gloss', 'exampleEs', 'exampleEn'].every(key => typeof sense[key] === 'string')
    && (sense.clozeEs === undefined || typeof sense.clozeEs === 'string')
    && strings(sense.acceptedEs) && strings(sense.acceptedEn) && typeof sense.accentsAreThePoint === 'boolean'
    && ['region', 'register'].every(key => notes[key] === undefined || typeof notes[key] === 'string')
}
export async function getSessionPlan(options: { maxNew?: number; budgetS?: number } = {}, signal?: AbortSignal): Promise<SessionPlan> {
  const query = new URLSearchParams()
  if (options.maxNew !== undefined) query.set('maxNew', String(options.maxNew))
  if (options.budgetS !== undefined) query.set('budgetS', String(options.budgetS))
  const body = await apiRequest(`/api/learn/session/plan${query.size ? `?${query}` : ''}`, { signal })
  return parseSessionPlan(body)
}
export function parseSessionPlan(body: unknown): SessionPlan {
  if (!object(body) || !['sessionId', 'generatedAtUtc', 'localDate'].every(key => typeof body[key] === 'string')
    || !Array.isArray(body.items) || !body.items.every(planItem) || !object(body.counts)
    || !['due', 'newOffered', 'backlog'].every(key => typeof (body.counts as Record<string, unknown>)[key] === 'number')
    || !strings(body.warnings) || (body.nextDueUtc !== undefined && typeof body.nextDueUtc !== 'string')) throw new ApiError('invalid-response')
  return body as SessionPlan
}
export async function postAttempts(attempts: readonly Attempt[]): Promise<{ results: AttemptResult[] }> {
  const body = await apiRequest('/api/learn/attempts', { method: 'POST', body: JSON.stringify({ attempts }) })
  if (!object(body) || !Array.isArray(body.results) || !body.results.every(item => object(item)
    && typeof item.clientAttemptId === 'string' && ['applied', 'duplicate', 'stale_epoch', 'suspended', 'out_of_order'].includes(String(item.status))
    && (item.card === undefined || (object(item.card) && Number.isSafeInteger(item.card.id)
      && Number.isSafeInteger(item.card.state) && typeof item.card.dueUtc === 'string'
      && typeof item.card.intervalDays === 'number' && typeof item.card.leech === 'boolean')))) throw new ApiError('invalid-response')
  return body as { results: AttemptResult[] }
}
export async function getLearnerWords(q: string, topic: string, signal?: AbortSignal): Promise<LearnerWord[]> {
  const query = new URLSearchParams({ q, topic })
  const body = await apiRequest(`/api/learn/words?${query}`, { signal })
  if (!object(body) || !Array.isArray(body.items) || !body.items.every(item => object(item)
    && ['key', 'headword', 'gloss', 'stage'].every(key => typeof item[key] === 'string')
    && (item.article === null || typeof item.article === 'string') && strings(item.topics))) throw new ApiError('invalid-response')
  return body.items as LearnerWord[]
}
export type Forecast = { tomorrow: number; next7Days: { date: string; due: number }[] }
export async function getForecast(signal?: AbortSignal): Promise<Forecast> {
  const body = await apiRequest('/api/learn/forecast', { signal })
  if (!object(body) || !Number.isSafeInteger(body.tomorrow) || Number(body.tomorrow) < 0
    || !Array.isArray(body.next7Days) || body.next7Days.length !== 7
    || !body.next7Days.every(day => object(day) && typeof day.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(day.date)
      && Number.isSafeInteger(day.due) && Number(day.due) >= 0)) throw new ApiError('invalid-response')
  return body as Forecast
}
