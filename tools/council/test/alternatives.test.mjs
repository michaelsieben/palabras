import assert from 'node:assert/strict';
import test from 'node:test';
import { mergeAlternatives, attributed, enoughAlternatives } from '../dist/alternatives.js';
import { merge } from '../dist/merge.js';
import { lint } from '../dist/lint.js';
import { runPipeline } from '../dist/pipeline.js';
import { replayAdapter } from '../dist/providers/replay.js';
import { proposal, critique, script, workspace, MODELS } from './helpers.mjs';

test('alternative dedupe counts unique models, ranked by support', () => {
  const alt = proposal().alternatives[0];
  const merged = mergeAlternatives([attributed(alt, MODELS[0]), attributed(alt, MODELS[0]), attributed(alt, MODELS[1]),
    attributed({ ...alt, en: 'different' }, MODELS[2])], MODELS);
  assert.equal(merged.length, 2);
  assert.equal(merged[0].support, 2);
  assert.deepEqual(merged[0].models, MODELS.slice(0, 2));
});

test('one top-up reuses critique prompt and merges suggestions, does not alter original score', async () => {
  const w = workspace();
  try {
    const replay = script([proposal('zorzal', 'noun', false)], ['es-zorzal-01']);
    replay[MODELS[0]].push(critique(['es-zorzal-01'], 'issue', proposal().alternatives));
    let topup = '';
    let count = 0;
    const result = await runPipeline({ ...w, request: {}, adapter: replayAdapter(replay, (model, prompt) => {
      if (model === MODELS[0] && ++count === 3) topup = prompt;
    }) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.ok(topup.startsWith('# critique.v1'));
    assert.equal(result.candidates[0].council.agreement, 1);
    assert.ok(enoughAlternatives(result.candidates[0]));
    assert.equal(result.candidates[0].alternatives[0].support, 1);
  } finally { w.cleanup(); }
});

test('top-up still short drops candidate and produces no importable bundle', async () => {
  const w = workspace();
  try {
    const replay = script([proposal('zorzal', 'noun', false)], ['es-zorzal-01']);
    replay[MODELS[0]].push(critique(['es-zorzal-01']));
    const result = await runPipeline({ ...w, request: {}, adapter: replayAdapter(replay) });
    assert.equal(result.manifest.status, 'failed');
    assert.equal(result.candidates.length, 0);
    assert.equal(result.dropped[0].reason, 'insufficient_alternatives');
  } finally { w.cleanup(); }
});

test('limits enforce 2-4 gloss and 1-3 examples without padded optional variants', async () => {
  const w = workspace();
  try {
    const word = proposal();
    word.alternatives.push(...Array.from({ length: 5 }, (_, i) => ({ field: 'gloss', sense: 0, en: `bird type ${i}`, rationale: 'Another useful gloss' })));
    const result = await runPipeline({ ...w, request: {}, adapter: replayAdapter(script([word], ['es-zorzal-01'])) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.equal(result.candidates[0].alternatives.filter((alt) => alt.field === 'gloss').length, 4);
    assert.equal(result.candidates[0].alternatives.filter((alt) => alt.field === 'regional_variant').length, 0);
    assert.ok(result.candidates[0].council.lint_actions.includes('alternative_limit:gloss:sense0'));
  } finally { w.cleanup(); }
});

test('shared lexicon lint swaps clean example/lemma, removes dirty alternatives, key unchanged', () => {
  const word = proposal('computadora');
  word.senses[0].example_es = 'Uso una computadora.';
  word.alternatives.push({ field: 'lemma', es: 'ordenador', rationale: 'Peninsular word instead' },
    { field: 'regional_variant', es: 'computadora', rationale: 'Another proposed spelling' });
  const candidate = merge([{ model: MODELS[0], candidate: word }], MODELS, 1);
  candidate.key = 'es-computadora-01';
  lint(candidate);
  assert.equal(candidate.key, 'es-computadora-01');
  assert.equal(candidate.lemma, 'ordenador');
  assert.equal(candidate.senses[0].example_es, 'El pájaro canta.');
  assert.ok(candidate.council.lint_actions.includes('swap_latam_primary:lemma'));
  assert.ok(candidate.council.lint_actions.includes('remove_latam_alternative:regional_variant:sense0'));
});

test('lint retains unresolved primary warning, vosotros allowed; ustedes/vos variants removed', () => {
  const word = proposal('computadora');
  word.senses[0].example_es = 'Ustedes miran el pájaro.';
  word.alternatives = [];
  const candidate = merge([{ model: MODELS[0], candidate: word }], MODELS, 1);
  lint(candidate);
  assert.ok(candidate.council.lint_warnings.includes('latam_primary:lemma'));
  assert.ok(candidate.council.lint_warnings.includes('latam_primary:example:sense0'));
});
