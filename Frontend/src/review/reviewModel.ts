import type { ReviewEdit, ReviewItem } from '../api/review.ts'
export function sittingKey(username: string) { return `palabras-review-sitting:${encodeURIComponent(username)}` }
export function parseSittingSize(value: string | null): number {
  const n = Number(value)
  return Number.isInteger(n) && n >= 5 && n <= 30 ? n : 15
}
export function batchEligible(word: ReviewItem) { return word.band === 'alta' && word.lintWarnings.length === 0 }
export function toggleBatch(selected: number[], id: number): number[] {
  return selected.includes(id) ? selected.filter(n => n !== id) : selected.length < 25 ? [...selected, id] : selected
}
export function editValues(word: ReviewItem): ReviewEdit[] {
  return [
    { field: 'lemma', after: word.headword }, { field: 'article', after: word.article ?? '' },
    { field: 'gender', after: word.gender ?? '' }, { field: 'regional_note', after: word.regionNotes ?? '' },
    ...word.senses.flatMap(s => [
      { field: 'gloss' as const, senseOrder: s.order, after: s.gloss },
      { field: 'example' as const, senseOrder: s.order, after: s.exampleEs },
      { field: 'accepted' as const, senseOrder: s.order, after: s.acceptedEs.join(', ') },
    ]),
  ]
}
export function changedEdits(original: ReviewEdit[], draft: ReviewEdit[]) {
  return draft.filter((edit, i) => edit.after !== original[i].after)
}
