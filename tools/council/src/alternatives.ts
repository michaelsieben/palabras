import { canonicalJson } from './bundle.js';
import type { CouncilAlternative, CouncilCandidate } from './bundle.js';
import type { SuggestedAlternative } from './normalise.js';
export function attributed(alternative: SuggestedAlternative, model: string): CouncilAlternative {
  return { ...alternative, sense: alternative.sense ?? 0, support: 1, models: [model] };
}
export function mergeAlternatives(alternatives: CouncilAlternative[], order: string[]): CouncilAlternative[] {
  const values = new Map<string, CouncilAlternative>();
  for (const alt of alternatives) {
    const id = canonicalJson([alt.field, alt.sense ?? 0, alt.es ?? '', alt.en ?? '']);
    const previous = values.get(id);
    const models = order.filter((model) => alt.models.includes(model) || previous?.models.includes(model));
    values.set(id, { ...(previous ?? alt), sense: alt.sense ?? 0, models, support: models.length });
  }
  return [...values.values()].sort((a, b) => b.support - a.support ||
    order.indexOf(a.models[0] ?? '') - order.indexOf(b.models[0] ?? '') || canonicalJson(a).localeCompare(canonicalJson(b)));
}
export function capAlternatives(candidate: CouncilCandidate): void {
  const counts = new Map<string, number>();
  const caps: Record<string, number> = { gloss: 4, example: 3, lemma: 2, regional_variant: 2, register_variant: 2 };
  candidate.alternatives = candidate.alternatives.filter((alt) => {
    const id = `${alt.sense ?? 0}|${alt.field}`;
    const count = (counts.get(id) ?? 0) + 1;
    counts.set(id, count);
    if (count > (caps[alt.field] ?? Infinity)) {
      candidate.council.lint_actions.push(`alternative_limit:${alt.field}:sense${alt.sense ?? 0}`);
      return false;
    }
    return true;
  });
}
export function enoughAlternatives(candidate: CouncilCandidate): boolean {
  return candidate.senses.every((_sense, index) => {
    const alts = candidate.alternatives.filter((alt) => (alt.sense ?? 0) === index);
    return alts.filter((alt) => alt.field === 'gloss').length >= 2 && alts.some((alt) => alt.field === 'example');
  });
}
