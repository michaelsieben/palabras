import type { CouncilCandidate, CouncilAlternative } from './bundle.js';
export type Proposal = Omit<CouncilCandidate, 'key' | 'council' | 'alternatives'> & { alternatives: SuggestedAlternative[] };
export type SuggestedAlternative = Omit<CouncilAlternative, 'support' | 'models'>;
export interface ModelProposal { model: string; candidate: Proposal }

export function withoutArticle(lemma: string): { lemma: string; article?: CouncilCandidate['article'] } {
  const text = lemma.normalize('NFC').toLowerCase().replace(/\s+/g, ' ').trim();
  const match = /^(el|la|los|las)\s+(.+)$/.exec(text);
  return match ? { lemma: match[2], article: match[1] as CouncilCandidate['article'] } : { lemma: text };
}
export function lemmaNorm(lemma: string): string { return withoutArticle(lemma).lemma; }
export function slug(lemma: string): string {
  return lemmaNorm(lemma).normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}
export function glossNorm(gloss: string): string {
  return gloss.normalize('NFC').toLowerCase().trim().replace(/^(to|a|the)\s+/, '').replace(/\s+/g, ' ');
}
export function normaliseProposal(candidate: Proposal): Proposal {
  const parsed = withoutArticle(candidate.lemma);
  const aliases: Record<string, string> = { n: 'noun', v: 'verb', adj: 'adjective', adv: 'adverb' };
  return { ...candidate, ...parsed, pos: aliases[candidate.pos.toLowerCase()] ?? candidate.pos.toLowerCase(),
    senses: candidate.senses.map((sense) => ({ ...sense, en_gloss: glossNorm(sense.en_gloss) })) };
}
