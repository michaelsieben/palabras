import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { runPipeline, assignKeys } from '../dist/pipeline.js';
import { replayAdapter } from '../dist/providers/replay.js';
import { loadProducerLexicon } from '../dist/lexicon.js';
import { merge } from '../dist/merge.js';
import { normaliseProposal, slug } from '../dist/normalise.js';
import { validateBundle } from '../dist/validate.js';
import { proposal, critique, script, workspace, writingAdapter, MODELS } from './helpers.mjs';

test('three vendors propose, merge, critique, score, lint and write a valid bundle', async () => {
  const w = workspace();
  try {
    const replay = script([proposal()], ['es-zorzal-01']);
    replay[MODELS[2]][0].text = JSON.stringify({ candidates: [{ ...proposal(), register: 'formal' }, { garbage: true }] });
    replay[MODELS[2]][1] = critique(['es-zorzal-01'], 'issue');
    const result = await runPipeline({ ...w, request: { topic: 'food', cefr: 'A2', count: 3 }, adapter: writingAdapter(replayAdapter(replay)) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.equal(result.manifest.models_requested, 3);
    assert.deepEqual(result.manifest.models_succeeded, { propose: 3, critique: 3 });
    assert.equal(result.manifest.reduced_confidence, false);
    assert.equal(result.manifest.models[2].invalid_count, 1);
    const candidate = result.candidates[0];
    assert.equal(candidate.register, 'neutral');
    assert.equal(candidate.council.agreement, 2 / 3);
    assert.equal(candidate.council.band, 'media');
    assert.equal(candidate.council.proposal_support, 1);
    assert.equal(candidate.alternatives[0].support, 3);
    assert.equal(candidate.senses[0].en_gloss, 'thrush');
    assert.equal(validateBundle(result.dir).valid, true);
  } finally { w.cleanup(); }
});

test('dry replay: corpus word exists before keying; homograph gets distinct NN', async () => {
  const w = workspace();
  try {
    const words = [proposal('zorzal', 'noun'), proposal('zorzal', 'adjective'), proposal('tender', 'verb')];
    const result = await runPipeline({ ...w, request: {}, dryRun: true, adapter: replayAdapter(script(words, ['es-zorzal-01', 'es-zorzal-02'])) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.deepEqual(result.candidates.map((item) => [item.key, item.pos]), [['es-zorzal-01', 'adjective'], ['es-zorzal-02', 'noun']]);
    assert.deepEqual(result.dropped, [{ lemma: 'tender', pos: 'verb', reason: 'exists' }]);
    assert.equal(existsSync(w.config.paths.outDir), false);
  } finally { w.cleanup(); }
});

test('committed bundles and override add occupied keys and existing lemma/POS pairs', () => {
  const w = workspace();
  try {
    const words = path.join(w.dir, 'words');
    const bundles = path.join(w.dir, 'council');
    mkdirSync(words);
    mkdirSync(path.join(bundles, 'one'), { recursive: true });
    writeFileSync(path.join(words, 'word.json'), JSON.stringify({ id: 'es-zorzal-01', headword: 'zorzal', partOfSpeech: 'noun' }));
    writeFileSync(path.join(bundles, 'one', 'candidates.jsonl'), JSON.stringify({ key: 'es-zorzal-03', lemma: 'zorzal', pos: 'verb' }) + '\n');
    const lexicon = loadProducerLexicon(words, bundles);
    assert.ok(lexicon.keys.has('es-tender-01'), 'override adds, never hides corpus');
    const candidates = ['adjective', 'noun', 'verb'].map((pos) => merge([{ model: MODELS[0], candidate: proposal('zorzal', pos) }], MODELS, 1));
    const result = assignKeys(candidates, lexicon);
    assert.equal(result.candidates[0].key, 'es-zorzal-02');
    assert.equal(result.dropped.filter((item) => item.reason === 'exists').length, 2);
  } finally { w.cleanup(); }
});

test('article stripping, accent-folded slugs, exhaustion and unkeyable lemmas', () => {
  assert.equal(slug('  la PeÑA azul '), 'pena-azul');
  assert.equal(normaliseProposal(proposal('la peña')).article, 'la');
  const candidates = ['año', 'ano', '☃'].map((lemma) => merge([{ model: MODELS[0], candidate: proposal(lemma) }], MODELS, 1));
  const lexicon = { keys: new Set(), entries: [], skipped: 0, directory: '' };
  const result = assignKeys(candidates, lexicon);
  assert.deepEqual(result.candidates.map((item) => item.key), ['es-ano-01', 'es-ano-02']);
  assert.equal(result.dropped[0].reason, 'unkeyable_lemma');
  const exhausted = assignKeys([merge([{ model: MODELS[0], candidate: proposal() }], MODELS, 1)], {
    ...lexicon, keys: new Set(Array.from({ length: 99 }, (_, i) => `es-zorzal-${String(i + 1).padStart(2, '0')}`)),
  });
  assert.equal(exhausted.dropped[0].reason, 'key_space_exhausted');
});

test('plurality ties use roster order; dissenting free-text values become alternatives', () => {
  const first = proposal();
  const second = proposal();
  second.register = 'formal';
  second.senses[0].en_gloss = 'different bird';
  second.senses[0].example_es = 'El pájaro vuela.';
  const result = merge([{ model: MODELS[1], candidate: second }, { model: MODELS[0], candidate: first }], MODELS, 3);
  assert.equal(result.register, 'neutral');
  assert.equal(result.council.proposal_support, 2 / 3);
  assert.ok(result.council.lint_actions.includes('plurality_tie:register'));
  assert.ok(result.alternatives.some((alt) => alt.en === 'different bird' && alt.models[0] === MODELS[1]));
  assert.ok(result.alternatives.some((alt) => alt.es === 'El pájaro vuela.'));
});

test('merge retains additional proposed senses and their per-sense alternatives', () => {
  const first = proposal();
  const second = proposal();
  second.senses.push({ en_gloss: 'another meaning', example_es: 'Este es otro ejemplo.', example_en: 'This is another example.' });
  second.alternatives.push(...proposal().alternatives.map((alt) => ({ ...alt, sense: 1 })));
  const result = merge([{ model: MODELS[0], candidate: first }, { model: MODELS[1], candidate: second }], MODELS, 2);
  assert.equal(result.senses.length, 2);
  assert.equal(result.senses[1].en_gloss, 'another meaning');
  assert.equal(result.alternatives.filter((alt) => alt.sense === 1).length, 3);
});

test('critique is batched at 25 and all successful batches determine phase success', async () => {
  const w = workspace();
  try {
    const words = Array.from({ length: 26 }, (_, i) => proposal(`zorzal${String(i).padStart(2, '0')}`));
    const keys = words.map((word) => `es-${word.lemma}-01`);
    const replay = Object.fromEntries(MODELS.map((model) => [model, [
      { text: JSON.stringify({ candidates: words }) }, critique(keys.slice(0, 25)), critique(keys.slice(25)),
    ]]));
    const result = await runPipeline({ ...w, request: { count: 30 }, adapter: writingAdapter(replayAdapter(replay)) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.equal(result.candidates.length, 26);
    assert.equal(readFileSync(path.join(result.dir, 'candidates.jsonl'), 'utf8').trim().split('\n').length, 26);
  } finally { w.cleanup(); }
});
