import type { CouncilCandidate } from './bundle.js';
export function score(candidate: CouncilCandidate): boolean {
  const critiques = candidate.council.critique;
  candidate.council.agreement = critiques.length === 0 ? 0 : critiques.filter((item) => item.verdict === 'ok').length / critiques.length;
  candidate.council.band = candidate.council.agreement >= 0.75 ? 'alta' : 'media';
  return candidate.council.agreement >= 0.5;
}
