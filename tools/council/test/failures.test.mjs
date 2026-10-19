import assert from 'node:assert/strict';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import path from 'node:path';
import test from 'node:test';
import { runPipeline } from '../dist/pipeline.js';
import { replayAdapter } from '../dist/providers/replay.js';
import { validateBundle } from '../dist/validate.js';
import { createProvider, PrivacyBlockedError, ProviderError } from '../dist/provider.js';
import { runCli } from '../dist/cli.js';
import { proposal, critique, script, workspace, writingAdapter, MODELS } from './helpers.mjs';

test('one provider auth failure continues with reduced confidence and healthy denominators', async () => {
  const w = workspace();
  try {
    const replay = script([proposal()], ['es-zorzal-01']);
    replay[MODELS[2]] = [{ error: 'auth' }, { error: 'auth' }];
    const result = await runPipeline({ ...w, request: {}, adapter: writingAdapter(replayAdapter(replay)) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.equal(result.manifest.reduced_confidence, true);
    assert.deepEqual(result.manifest.models_succeeded, { propose: 2, critique: 2 });
    assert.equal(result.candidates[0].council.agreement, 1);
    assert.equal(result.candidates[0].council.proposal_support, 1);
    assert.equal(result.manifest.models[2].phases.propose, 'failed');
  } finally { w.cleanup(); }
});

for (const phase of ['propose', 'critique']) {
  test(`COUNCIL_MIN_MODELS above healthy ${phase}: failed manifest, no candidates, validator refusal`, async () => {
    const w = workspace({ minModels: 3 });
    try {
      const replay = script([proposal()], ['es-zorzal-01']);
      replay[MODELS[2]][phase === 'propose' ? 0 : 1] = { error: 'auth' };
      const result = await runPipeline({ ...w, request: {}, adapter: writingAdapter(replayAdapter(replay)) });
      assert.equal(result.manifest.status, 'failed');
      assert.match(result.manifest.error, new RegExp(`${phase}: healthy models below COUNCIL_MIN_MODELS`));
      assert.equal(existsSync(path.join(result.dir, 'candidates.jsonl')), false);
      assert.equal(validateBundle(result.dir).valid, false);
    } finally { w.cleanup(); }
  });
}

test('transient errors retry twice with exponential backoff; auth does not retry', async () => {
  const w = workspace();
  try {
    const delays = [];
    let calls = 0;
    const provider = createProvider(w.config, w.policy, async () => {
      calls++;
      if (calls < 3) throw new ProviderError('http', 429);
      return { text: '{}' };
    }, async (ms) => delays.push(ms));
    await provider.send(MODELS[0], 'clean prompt');
    assert.equal(calls, 3);
    assert.deepEqual(delays, [1000, 2000]);
    calls = 0;
    await assert.rejects(createProvider(w.config, w.policy, async () => { calls++; throw new ProviderError('auth', 401); }).send(MODELS[0], 'clean'), /provider auth HTTP 401/);
    assert.equal(calls, 1);
  } finally { w.cleanup(); }
});

test('schema-invalid JSON retries once; partial invalid proposals counted without rejecting model', async () => {
  const w = workspace();
  try {
    const replay = script([proposal()], ['es-zorzal-01']);
    replay[MODELS[0]].unshift({ text: 'not JSON' });
    const prompts = [];
    const result = await runPipeline({ ...w, request: {}, adapter: writingAdapter(replayAdapter(replay, (_model, prompt) => prompts.push(prompt))) });
    assert.equal(result.manifest.status, 'ok', result.manifest.error);
    assert.equal(result.manifest.models[0].invalid_count, 1);
    assert.ok(prompts.some((prompt) => prompt.endsWith('Return valid JSON matching the schema. No prose or unknown fields.')));
  } finally { w.cleanup(); }
});

test('private model output aborts whole run before critique, no dirty committed artefacts', async () => {
  const w = workspace();
  try {
    const replay = script([proposal()], ['es-zorzal-01']);
    const dirty = proposal();
    dirty.senses[0].example_es = 'synthetic forbidden sentinel.';
    replay[MODELS[0]][0] = { text: JSON.stringify({ candidates: [dirty] }) };
    let critiqueCalls = 0;
    const result = await runPipeline({ ...w, request: {}, adapter: writingAdapter(replayAdapter(replay, (_model, prompt) => { if (prompt.startsWith('# critique')) critiqueCalls++; })) });
    assert.equal(result.manifest.status, 'failed');
    assert.equal(critiqueCalls, 0);
    assert.equal(existsSync(path.join(result.dir, 'dropped.jsonl')), false);
    assert.equal(existsSync(path.join(result.dir, 'candidates.jsonl')), false);
    assert.doesNotMatch(readFileSync(path.join(result.dir, 'manifest.json'), 'utf8'), /synthetic forbidden sentinel/);
  } finally { w.cleanup(); }
});

test('real CLI planted style text reports privacy failure before any adapter call or write', async () => {
  const w = workspace();
  try {
    const guide = path.join(w.dir, 'guide.md');
    writeFileSync(guide, 'synthetic forbidden sentinel.');
    const err = [];
    let calls = 0;
    const code = await runCli(['propose', '--privacy', w.config.paths.privacy, '--style-guide', guide,
      '--out', w.config.paths.outDir, '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {},
      async () => { calls++; return { text: '{}' }; });
    assert.equal(code, 1);
    assert.equal(calls, 0);
    assert.equal(existsSync(w.config.paths.outDir), false);
    assert.match(err.join('\n'), /PrivacyBlockedError: privacy guard blocked/);
    assert.match(err.join('\n'), /guide\.md contains a private term \(local #1\)/);
    assert.doesNotMatch(err.join('\n'), /synthetic forbidden sentinel/);
  } finally { w.cleanup(); }
});

test('provider failures redact exception text and keys from manifests', async () => {
  const w = workspace();
  try {
    const result = await runPipeline({ ...w, request: {}, adapter: async () => { throw new Error('UNSAFE secret body or key'); } });
    assert.equal(result.manifest.status, 'failed');
    assert.doesNotMatch(JSON.stringify(result.manifest), /UNSAFE|secret body/);
    assert.match(JSON.stringify(result.manifest), /provider network/);
  } finally { w.cleanup(); }
});

test('real CLI rendered prompt failure reports its message and exits before sends or writes', async () => {
  const w = workspace();
  try {
    const guide = path.join(w.dir, 'guide.md');
    writeFileSync(guide, '{{UNSAFE_PLACEHOLDER}}');
    const err = [];
    let calls = 0;
    const code = await runCli(['propose', '--privacy', w.config.paths.privacy, '--style-guide', guide,
      '--out', w.config.paths.outDir, '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {},
      async () => { calls++; return { text: '{}' }; });
    assert.equal(code, 1);
    assert.equal(calls, 0);
    assert.equal(existsSync(w.config.paths.outDir), false);
    assert.match(err.join('\n'), /leftover prompt placeholder/);
    assert.doesNotMatch(err.join('\n'), /UNSAFE_PLACEHOLDER/);
  } finally { w.cleanup(); }
});

test('CLI output-operation catch preserves safe errno without echoing exception paths', async () => {
  const w = workspace();
  try {
    const blockedOut = path.join(w.dir, 'UNSAFE-output-path');
    writeFileSync(blockedOut, 'not a directory');
    const err = [];
    const code = await runCli(['propose', '--privacy', w.config.paths.privacy,
      '--out', blockedOut, '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {},
      writingAdapter(replayAdapter(script([proposal()], ['es-zorzal-01']))));
    assert.equal(code, 1);
    assert.match(err.join('\n'), /council generation failed .*\[Error code=ENOTDIR\]/);
    assert.doesNotMatch(err.join('\n'), /UNSAFE-output-path/);
  } finally { w.cleanup(); }
});

test('CLI rejected-pipeline diagnostics distinguish network and privacy errors without their messages', async () => {
  const w = workspace();
  const original = fs.mkdirSync;
  try {
    for (const [error, label] of [
      [new ProviderError('network'), 'ProviderError:network'],
      [new PrivacyBlockedError('UNSAFE-secret-message'), 'PrivacyBlockedError'],
    ]) {
      // Force a rejection outside pipeline's failed-manifest handling to exercise CLI's catch.
      fs.mkdirSync = () => { throw error; };
      syncBuiltinESMExports();
      const err = [];
      const code = await runCli(['propose', '--privacy', w.config.paths.privacy,
        '--out', w.config.paths.outDir, '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {},
        writingAdapter(replayAdapter(script([proposal()], ['es-zorzal-01']))));
      assert.equal(code, 1);
      assert.ok(err.join('\n').includes(`[${label}]`), err.join('\n'));
      assert.doesNotMatch(err.join('\n'), /UNSAFE-secret-message/);
    }
  } finally { fs.mkdirSync = original; syncBuiltinESMExports(); w.cleanup(); }
});

test('critique below half drops candidate, never queued', async () => {
  const w = workspace();
  try {
    const replay = script([proposal()], ['es-zorzal-01']);
    replay[MODELS[1]][1] = critique(['es-zorzal-01'], 'issue');
    replay[MODELS[2]][1] = critique(['es-zorzal-01'], 'issue');
    const result = await runPipeline({ ...w, request: {}, dryRun: true, adapter: replayAdapter(replay) });
    assert.equal(result.manifest.status, 'failed');
    assert.equal(result.candidates.length, 0);
    assert.equal(result.dropped[0].reason, 'low_agreement');
  } finally { w.cleanup(); }
});

test('propose --hold is rejected; real run refuses empty local list before adapter call', async () => {
  const out = [];
  const io = { out() {}, err: (line) => out.push(line) };
  assert.equal(await runCli(['propose', '--dry-run', '--hold'], io, {}), 1);
  assert.match(out.join('\n'), /unknown option --hold/);
  const w = workspace();
  try {
    let calls = 0;
    const result = await runCli(['propose', '--privacy', path.join(w.dir, 'absent.json')], io, {}, async () => { calls++; return { text: '{}' }; });
    assert.equal(result, 1);
    assert.equal(calls, 0);
  } finally { w.cleanup(); }
});
