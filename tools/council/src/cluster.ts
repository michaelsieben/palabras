import { lemmaNorm } from './normalise.js';
import type { ModelProposal } from './normalise.js';
export function cluster(proposals: ModelProposal[]): ModelProposal[][] {
  const clusters = new Map<string, ModelProposal[]>();
  for (const item of proposals) {
    const identity = JSON.stringify([lemmaNorm(item.candidate.lemma), item.candidate.pos]);
    const group = clusters.get(identity) ?? [];
    // One model gets one vote, even if it repeats an item in its response.
    if (!group.some((entry) => entry.model === item.model)) group.push(item);
    clusters.set(identity, group);
  }
  return [...clusters.values()];
}
