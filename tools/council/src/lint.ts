import { createRequire } from 'node:module';
import path from 'node:path';
import { REPO_ROOT } from './config.js';
import type { CouncilCandidate } from './bundle.js';
const require = createRequire(import.meta.url);
const { containsTerm, fold, loadLexicon } = require('../../content-lint/lexicon.js') as {
  containsTerm: (text: string, term: string) => boolean;
  fold: (text: string) => string;
  loadLexicon: (file: string) => { denylist: { term: string; matchIn?: string[] }[]; allowlist: { term: string }[] };
};
const lexicon = loadLexicon(path.join(REPO_ROOT, 'content/style-guide.lexicon.json'));

export function lint(candidate: CouncilCandidate): void {
  function dirty(text: string | undefined, scope: 'headword' | 'example'): boolean {
    if (!text) return false;
    if (scope === 'headword' && lexicon.allowlist.some((entry) => fold(entry.term) === fold(text))) return false;
    return lexicon.denylist.some((rule) => (rule.matchIn ?? ['headword', 'example']).includes(scope) && containsTerm(text, rule.term)) ||
      (scope === 'example' && (containsTerm(text, 'ustedes') || containsTerm(text, 'vos')));
  }
  candidate.alternatives = candidate.alternatives.filter((alt) => {
    const hit = dirty(alt.es, alt.field === 'example' ? 'example' : 'headword');
    if (hit) candidate.council.lint_actions.push(`remove_latam_alternative:${alt.field}:sense${alt.sense ?? 0}`);
    return !hit;
  });
  // Keys are immutable even when lint swaps a headword.
  if (dirty(candidate.lemma, 'headword')) {
    const replacement = candidate.alternatives.find((alt) => alt.field === 'lemma' && alt.es);
    if (replacement) {
      candidate.lemma = replacement.es!;
      candidate.alternatives = candidate.alternatives.filter((alt) => alt !== replacement);
      candidate.council.lint_actions.push('swap_latam_primary:lemma');
    } else candidate.council.lint_warnings.push('latam_primary:lemma');
  }
  for (const [index, sense] of candidate.senses.entries()) {
    if (dirty(sense.example_es, 'example')) {
      const replacement = candidate.alternatives.find((alt) => alt.field === 'example' && (alt.sense ?? 0) === index);
      if (replacement) {
        sense.example_es = replacement.es!;
        sense.example_en = replacement.en!;
        candidate.alternatives = candidate.alternatives.filter((alt) => alt !== replacement);
        candidate.council.lint_actions.push(`swap_latam_primary:example:sense${index}`);
      } else candidate.council.lint_warnings.push(`latam_primary:example:sense${index}`);
    }
  }
}
