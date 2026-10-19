import { createRequire } from 'node:module';
import type { CouncilCandidate } from './bundle.js';
import type { SuggestedAlternative } from './normalise.js';
import { ProviderError } from './provider.js';
const require = createRequire(import.meta.url);
const { validate } = require('../../content-lint/validate.js') as { validate: (schema: unknown, data: unknown, root: unknown, at: string, errors: string[]) => void };
export const ISSUE_CODES = ['latam_lexicon', 'unnatural_es', 'wrong_gloss', 'wrong_gender', 'cefr_mismatch', 'register', 'duplicate_meaning'];
export interface CritiqueEntry { key: string; verdict: 'ok' | 'issue'; issues?: string[]; suggested_alternatives?: SuggestedAlternative[] }

export function validAlternative(alt: unknown, schema: any, senses: number, knownKeys: ReadonlySet<string>): alt is SuggestedAlternative {
  const errors: string[] = [];
  validate(schema.$defs.alternative, alt, schema, 'alternative', errors);
  if (errors.length) return false;
  const value = alt as SuggestedAlternative;
  return (value.sense ?? 0) < senses &&
    (value.target_key === undefined || knownKeys.has(value.target_key)) &&
    (value.field !== 'example' || (!!value.es && !!value.en)) &&
    (value.field !== 'gloss' || !!value.en) &&
    (!['lemma', 'regional_variant', 'register_variant', 'regional_note'].includes(value.field) || !!value.es);
}
export function parseCritique(text: string, candidates: CouncilCandidate[], schema: any, knownKeys: ReadonlySet<string>): CritiqueEntry[] {
  let document: { critiques?: unknown } | null;
  try { document = JSON.parse(text); } catch { throw new ProviderError('invalid'); }
  if (!Array.isArray(document?.critiques) || document.critiques.length !== candidates.length) throw new ProviderError('invalid');
  const seen = new Set<string>();
  const entries: CritiqueEntry[] = [];
  for (const value of document.critiques) {
    if (!value || typeof value !== 'object') throw new ProviderError('invalid');
    const item = value as CritiqueEntry;
    const candidate = candidates.find((entry) => entry.key === item.key);
    if (!candidate || seen.has(item.key) || !['ok', 'issue'].includes(item.verdict)) throw new ProviderError('invalid');
    if (item.issues !== undefined && (!Array.isArray(item.issues) || item.issues.some((code) => !ISSUE_CODES.includes(code)))) throw new ProviderError('invalid');
    if (item.verdict === 'issue' && !item.issues?.length) throw new ProviderError('invalid');
    if (item.suggested_alternatives !== undefined && (!Array.isArray(item.suggested_alternatives) ||
      item.suggested_alternatives.some((alt) => !validAlternative(alt, schema, candidate.senses.length, knownKeys)))) throw new ProviderError('invalid');
    seen.add(item.key);
    // Whitelist response fields; no model-provided diagnostics survive.
    entries.push({ key: item.key, verdict: item.verdict, ...(item.verdict === 'issue' ? { issues: item.issues } : {}),
      suggested_alternatives: item.suggested_alternatives ?? [] });
  }
  return entries;
}
