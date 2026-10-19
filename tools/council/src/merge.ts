import { canonicalJson } from './bundle.js';
import type { CouncilCandidate } from './bundle.js';
import type { ModelProposal } from './normalise.js';
import { attributed, mergeAlternatives } from './alternatives.js';

/** Plurality, stable roster tie-break. Free-text variants become attributed chips. */
export function merge(group: ModelProposal[], order: string[], healthy: number): CouncilCandidate {
  const sorted = [...group].sort((a, b) => order.indexOf(a.model) - order.indexOf(b.model));
  const actions: string[] = [];
  function vote(values: Array<{ model: string; value: unknown }>, field: string): unknown {
    const counts = new Map<string, { value: unknown; count: number }>();
    for (const { value } of values) {
      const id = canonicalJson(value) ?? 'undefined';
      const entry = counts.get(id) ?? { value, count: 0 };
      entry.count++;
      counts.set(id, entry);
    }
    const ranked = [...counts.values()].sort((a, b) => b.count - a.count);
    if (ranked.length > 1 && ranked[0].count === ranked[1].count) actions.push(`plurality_tie:${field}`);
    return ranked[0].value;
  }
  const primary = structuredClone(sorted[0].candidate);
  for (const field of ['article', 'gender', 'cefr', 'register', 'region', 'forms'] as const) {
    Object.assign(primary, { [field]: vote(sorted.map((item) => ({ model: item.model, value: item.candidate[field] })), field) });
  }
  const alternativeInputs = sorted.flatMap((item) => item.candidate.alternatives.map((alt) => attributed(alt, item.model)));
  // Align by schema sense index. Keep the longest valid shape so no proposed sense disappears;
  // critique can flag duplicate_meaning/wrong_gloss instead of a heuristic inventing alignment.
  const senseShape = [...sorted].sort((a, b) => b.candidate.senses.length - a.candidate.senses.length)[0].candidate.senses;
  primary.senses = senseShape.map((sense, index) => {
    const available = sorted.filter((item) => item.candidate.senses[index]);
    const gloss = vote(available.map((item) => ({ model: item.model, value: item.candidate.senses[index].en_gloss })), `gloss:${index}`) as string;
    const example = vote(available.map((item) => ({ model: item.model, value: [item.candidate.senses[index].example_es, item.candidate.senses[index].example_en] })), `example:${index}`) as string[];
    for (const item of available) {
      const variant = item.candidate.senses[index];
      if (variant.en_gloss !== gloss) alternativeInputs.push(attributed({ field: 'gloss', sense: index, en: variant.en_gloss, rationale: 'Distinct proposal gloss' }, item.model));
      if (variant.example_es !== example[0] || variant.example_en !== example[1]) alternativeInputs.push(attributed({ field: 'example', sense: index, es: variant.example_es, en: variant.example_en, rationale: 'Distinct proposal example' }, item.model));
    }
    return { ...sense, en_gloss: gloss, example_es: example[0], example_en: example[1] };
  });
  return { ...primary, key: '', alternatives: mergeAlternatives(alternativeInputs, order),
    council: { run_id: 'pending', agreement: 0, band: 'media', proposal_support: sorted.length / healthy,
      proposed_by: sorted.map((item) => item.model), critique: [], lint_warnings: [], lint_actions: actions } };
}
