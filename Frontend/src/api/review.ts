import { ApiError, apiRequest } from './client.ts'

export type AlternativeField = 'lemma' | 'gloss' | 'example' | 'regional_variant' | 'register_variant'
export type ReviewField = AlternativeField | 'regional_note' | 'article' | 'gender' | 'accepted' | 'whole'
export type ReviewSense = { order: number; gloss: string; acceptedEn: string[]; acceptedEs: string[]; exampleEs: string; exampleEn: string; notes?: string }
export type ReviewItem = {
  wordId: number; key: string; version: number; headword: string; article?: string; gender?: string
  pos: string; cefr: string; topic?: string; senses: ReviewSense[]; notes?: string; regionNotes?: string; registerNotes?: string
  agreement?: number; band?: 'alta' | 'media'; reducedConfidence: boolean; lintWarnings: string[]
  alternatives: { id: number; field: AlternativeField; text: string; translation?: string; reason: string; support?: string }[]
  lastFlag?: { action: string; note?: string; at: string }
}
export type ApprovedWord = { wordId: number; key: string; version: number; headword: string; article?: string; gloss: string; topic?: string; approvedAt: string }
export type ReviewQueueResponse = { items: ReviewItem[]; pendingTotal: number; topics: { topic: string; pending: number }[] }
export type ReviewEdit = { field: ReviewField; senseOrder?: number; after: string }
export type ReviewAction = { clientReviewId: string; wordId: number; wordVersion: number
  action: 'approve' | 'approve_alt' | 'approve_edit' | 'reject' | 'needs_change' | 'demote'
  field?: ReviewField; senseOrder?: number; alternativeId?: number; edits?: ReviewEdit[]; note?: string; decisionMs?: number; viaBatch?: boolean }
export type ReviewResult = { clientReviewId: string; result: 'applied' | 'duplicate' | 'stale_version' | 'invalid_transition' | 'invalid'; word?: { status: string; version: number } }
const object = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null
const integer = (v: unknown, min = 0) => Number.isSafeInteger(v) && Number(v) >= min
const strings = (v: unknown) => Array.isArray(v) && v.every(x => typeof x === 'string')
const optional = (v: Record<string, unknown>, keys: string[]) => keys.every(k => v[k] === undefined || typeof v[k] === 'string')
const date = (v: unknown) => typeof v === 'string' && Number.isFinite(Date.parse(v))
export function isApprovedWord(v: unknown): v is ApprovedWord {
  return object(v) && integer(v.wordId, 1) && integer(v.version, 1) && ['key', 'headword', 'gloss'].every(k => typeof v[k] === 'string') && optional(v, ['article', 'topic']) && date(v.approvedAt)
}
export function isReviewItem(v: unknown): v is ReviewItem {
  return object(v) && integer(v.wordId, 1) && integer(v.version, 1) && ['key', 'headword', 'pos', 'cefr'].every(k => typeof v[k] === 'string')
    && optional(v, ['article', 'gender', 'topic', 'notes', 'regionNotes', 'registerNotes']) && typeof v.reducedConfidence === 'boolean'
    && (v.agreement === undefined || typeof v.agreement === 'number' && Number.isFinite(v.agreement) && v.agreement >= 0 && v.agreement <= 1)
    && (v.band === undefined || v.band === 'alta' || v.band === 'media') && strings(v.lintWarnings)
    && Array.isArray(v.senses) && v.senses.length > 0 && v.senses.every(s => object(s) && integer(s.order) && ['gloss', 'exampleEs', 'exampleEn'].every(k => typeof s[k] === 'string') && strings(s.acceptedEn) && strings(s.acceptedEs) && optional(s, ['notes']))
    && Array.isArray(v.alternatives) && v.alternatives.every(a => object(a) && integer(a.id, 1) && ['lemma', 'gloss', 'example', 'regional_variant', 'register_variant'].includes(String(a.field)) && typeof a.text === 'string' && typeof a.reason === 'string' && optional(a, ['translation', 'support']))
    && (v.lastFlag === undefined || object(v.lastFlag) && typeof v.lastFlag.action === 'string' && optional(v.lastFlag, ['note']) && date(v.lastFlag.at))
}
export function parseReviewQueue(body: unknown): ReviewQueueResponse {
  if (!object(body) || !Array.isArray(body.items) || !body.items.every(isReviewItem) || !integer(body.pendingTotal)
    || !Array.isArray(body.topics) || !body.topics.every(t => object(t) && typeof t.topic === 'string' && integer(t.pending))) throw new ApiError('invalid-response')
  return body as ReviewQueueResponse
}
export async function getReviewQueue(signal?: AbortSignal): Promise<ReviewQueueResponse> {
  return parseReviewQueue(await apiRequest('/api/review/queue?limit=30', { signal }))
}
export const approvedPageSize = 30
export async function getApprovedWords(q: string, after?: string, signal?: AbortSignal): Promise<{ items: ApprovedWord[]; nextCursor?: string }> {
  const query = new URLSearchParams({ q, limit: String(approvedPageSize) })
  if (after) query.set('after', after)
  const body = await apiRequest(`/api/review/approved?${query}`, { signal })
  if (!object(body) || !Array.isArray(body.items) || !body.items.every(isApprovedWord)) throw new ApiError('invalid-response')
  const items = body.items as ApprovedWord[]
  return { items, nextCursor: items.length === approvedPageSize ? String(items.at(-1)!.wordId) : undefined }
}
export async function postReviewActions(actions: readonly ReviewAction[]): Promise<{ results: ReviewResult[] }> {
  const body = await apiRequest('/api/review/actions', { method: 'POST', body: JSON.stringify({ actions }) })
  if (!object(body) || !Array.isArray(body.results) || !body.results.every(r => object(r) && typeof r.clientReviewId === 'string'
    && ['applied', 'duplicate', 'stale_version', 'invalid_transition', 'invalid'].includes(String(r.result))
    && (r.word === undefined || object(r.word) && ['approved', 'pending_review', 'needs_change', 'rejected', 'draft', 'retired'].includes(String(r.word.status)) && integer(r.word.version, 1)))) throw new ApiError('invalid-response')
  return body as { results: ReviewResult[] }
}
