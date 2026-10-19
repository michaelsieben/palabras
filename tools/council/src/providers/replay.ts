import { readFileSync } from 'node:fs';
import { ProviderError } from '../provider.js';
import type { ProviderAdapter, ProviderReply } from '../provider.js';
export type ReplayItem = ProviderReply | { error: 'auth' | 'http' | 'timeout' | 'invalid'; status?: number };
export type ReplayScript = Record<string, ReplayItem[]>;
/** One response per invocation, in each model's call order. Never touches network. */
export function replayAdapter(script: ReplayScript, onCall?: (model: string, prompt: string) => void): ProviderAdapter {
  const queues = structuredClone(script);
  return async (model, prompt) => {
    onCall?.(model, prompt);
    const item = queues[model]?.shift();
    if (!item) throw new ProviderError('invalid');
    if ('error' in item) throw new ProviderError(item.error, item.status);
    return item;
  };
}
export function readReplay(file: string): ProviderAdapter {
  try { return replayAdapter(JSON.parse(readFileSync(file, 'utf8'))); }
  catch { throw new Error('replay file is not readable JSON'); }
}
