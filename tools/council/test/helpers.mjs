import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { loadConfig } from '../dist/config.js';
import { loadPrivacyPolicy } from '../dist/privacy.js';

export const MODELS = ['anthropic:fixture', 'openai:fixture', 'google:fixture'];
export function proposal(lemma = 'zorzal', pos = 'noun', alternatives = true) {
  return { lemma, pos, ...(pos === 'noun' ? { article: 'el', gender: 'masculine' } : {}), cefr: 'A2', topic: 'food', region: 'ES', register: 'neutral',
    senses: [{ en_gloss: 'A thrush', example_es: 'Veo un pájaro en el jardín.', example_en: 'I see a bird in the garden.' }],
    alternatives: alternatives ? [
      { field: 'gloss', sense: 0, en: 'songbird', rationale: 'Another defensible gloss' },
      { field: 'gloss', sense: 0, en: 'thrush bird', rationale: 'Another defensible gloss' },
      { field: 'example', sense: 0, es: 'El pájaro canta.', en: 'The bird sings.', rationale: 'Another natural example' },
    ] : [] };
}
export function critique(keys, verdict = 'ok', suggestions = []) {
  return { text: JSON.stringify({ critiques: keys.map((key) => ({ key, verdict, ...(verdict === 'issue' ? { issues: ['register'] } : {}), suggested_alternatives: suggestions })) }) };
}
export function script(words, keys) {
  return Object.fromEntries(MODELS.map((id) => [id, [{ text: JSON.stringify({ candidates: words }) }, critique(keys)]]));
}
export function workspace(overrides = {}) {
  const dir = mkdtempSync(path.join(os.tmpdir(), 'palabras-c7-'));
  const privacy = path.join(dir, 'privacy.local.json');
  writeFileSync(privacy, JSON.stringify({ terms: ['synthetic forbidden sentinel'] }));
  const config = loadConfig({ env: {}, envFile: path.join(dir, 'missing.env'), outDir: path.join(dir, 'bundles'), privacy, models: MODELS, ...overrides });
  const policy = loadPrivacyPolicy({ localFile: privacy, hashFile: config.paths.privacyHashes, requireLocal: true });
  return { dir, config, policy, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
}
