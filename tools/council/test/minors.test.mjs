import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { runCli } from '../dist/cli.js';
import { runPipeline } from '../dist/pipeline.js';
import { replayAdapter } from '../dist/providers/replay.js';
import { checkPrivacyHashes, PrivacyNotConfiguredError } from '../dist/privacy-check.js';
import { privacyHashDocument, PrivacyConfigError } from '../dist/privacy.js';
import { proposal, script, workspace, MODELS } from './helpers.mjs';

test('real run summary reports every intentional skip and manifest counts match audit records', async () => {
  const w = workspace();
  try {
    const out = [], err = [];
    const code = await runCli(['propose', '--privacy', w.config.paths.privacy,
      '--privacy-hashes', w.config.paths.privacyHashes, '--env-file', path.join(w.dir, 'missing.env'),
      '--models', MODELS.join(','), '--out', w.config.paths.outDir],
    { out: (line) => out.push(line), err: (line) => err.push(line) }, {},
    replayAdapter(script([proposal(), proposal('tender', 'verb')], ['es-zorzal-01'])));
    assert.equal(code, 0, err.join('\n'));
    assert.match(out.join('\n'), /dropped: .*"reason":"exists".*"lemma":"tender"/);
    const dir = out.find((line) => line.startsWith('bundle=')).slice(7);
    const manifest = JSON.parse(readFileSync(path.join(dir, 'manifest.json'), 'utf8'));
    const dropped = readFileSync(path.join(dir, 'dropped.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
    assert.equal(manifest.counts.dropped, 1);
    assert.deepEqual(dropped, [{ lemma: 'tender', pos: 'verb', reason: 'exists' }]);
    console.log(`skip probe: status=${manifest.status} manifest.counts.dropped=${manifest.counts.dropped} dropped.jsonl reason=${dropped[0].reason} lemma=${dropped[0].lemma}`);
  } finally { w.cleanup(); }
});

test('invalid and count-limited proposals have model/index diagnostics without raw text', async () => {
  const w = workspace();
  try {
    const result = await runPipeline({ ...w, request: { count: 2 }, dryRun: true,
      adapter: replayAdapter(script([proposal(), { garbage: 'do-not-echo' }, proposal('extra')], ['es-zorzal-01'])) });
    assert.equal(result.manifest.status, 'ok');
    assert.equal(result.dropped.filter((drop) => drop.reason === 'invalid_proposal').length, 3);
    assert.equal(result.dropped.filter((drop) => drop.reason === 'request_count_limit').length, 3);
    assert.ok(result.dropped.every((drop) => drop.model && drop.proposal_index));
    assert.doesNotMatch(JSON.stringify(result), /do-not-echo/);
  } finally { w.cleanup(); }
});

test('hash guard fails distinctively on deliberate empty policy versus missing/broken config', () => {
  const w = workspace();
  try {
    assert.throws(() => checkPrivacyHashes(w.config.paths.privacyHashes, []), PrivacyNotConfiguredError);
    assert.throws(() => checkPrivacyHashes(path.join(w.dir, 'missing.json'), []), PrivacyConfigError);
    writeFileSync(w.config.paths.privacyHashes, '{broken');
    assert.throws(() => checkPrivacyHashes(w.config.paths.privacyHashes, []), PrivacyConfigError);
    console.log('empty hash probe: PrivacyNotConfiguredError; missing/malformed: PrivacyConfigError');
  } finally { w.cleanup(); }
});

test('non-empty hash guard scans content without reading local policy', () => {
  const w = workspace();
  try {
    writeFileSync(w.config.paths.privacyHashes, JSON.stringify(privacyHashDocument(w.config.paths.privacy)));
    writeFileSync(w.config.paths.privacy, '{unreadable local file');
    assert.equal(checkPrivacyHashes(w.config.paths.privacyHashes, [{ field: 'clean', text: 'Clean text.' }]), 1);
    assert.throws(() => checkPrivacyHashes(w.config.paths.privacyHashes,
      [{ field: 'probe', text: 'synthetic forbidden sentinel' }]), /probe contains a private term \(hashed #1\)/);
  } finally { w.cleanup(); }
});

test('10-token term export fails with explicit index/count/limit and no document or term echo', async () => {
  const w = workspace();
  try {
    const term = Array.from({ length: 10 }, (_, index) => `synthetic${index}`).join(' ');
    writeFileSync(w.config.paths.privacy, JSON.stringify({ terms: [term] }));
    const out = [], err = [];
    assert.equal(await runCli(['privacy', 'hash', '--privacy', w.config.paths.privacy],
      { out: (line) => out.push(line), err: (line) => err.push(line) }, {}), 1);
    assert.equal(out.length, 0);
    assert.match(err.join('\n'), /term #1 has 10 tokens; hash export supports 1-8 tokens; no hash document was exported/);
    assert.doesNotMatch(err.join('\n'), /synthetic/);
    console.log(err.join('\n'));
  } finally { w.cleanup(); }
});
