import assert from 'node:assert/strict';
import { writeFileSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { privacyHashDocument, loadPrivacyPolicy, scanForPrivateTerms, PrivacyConfigError, PRIVACY_SCHEMA, PRIVACY_NORMALISATION } from '../dist/privacy.js';
import { runCli } from '../dist/cli.js';
import { createProvider } from '../dist/provider.js';
import { workspace, MODELS } from './helpers.mjs';

test('generator sorted, deduplicated, same coarse/fine tokens as scanner; CLI emits only hashes', async () => {
  const w = workspace();
  try {
    const file = w.config.paths.privacy;
    writeFileSync(file, JSON.stringify({ terms: ['Synthetic Example', 'synthetic-example', 'probe@example.invalid', 'Example.invalid'] }));
    const document = privacyHashDocument(file);
    assert.equal(document.schema, PRIVACY_SCHEMA);
    assert.equal(document.normalisation, PRIVACY_NORMALISATION);
    assert.equal(document.terms.length, 3);
    assert.deepEqual(document.terms.map((entry) => entry.sha256), document.terms.map((entry) => entry.sha256).sort());
    const out = [], err = [];
    assert.equal(await runCli(['privacy', 'hash', '--privacy', file], { out: (line) => out.push(line), err: (line) => err.push(line) }, {}), 0);
    assert.deepEqual(JSON.parse(out[0]), document);
    assert.doesNotMatch(out[0], /Synthetic|probe@example/i);
    assert.equal(err.length, 0);
  } finally { w.cleanup(); }
});

for (const text of ['Synthetic Example.', 'Synthetic-Example', 'Synthetic_Example', '<probe@example.invalid>.', 'Visit example.invalid.']) {
  test(`hash-only scan catches punctuation/separator variant ${text.replace(/\w+/g, 'x')}`, () => {
    const w = workspace();
    try {
      writeFileSync(w.config.paths.privacy, JSON.stringify({ terms: ['Synthetic Example', 'probe@example.invalid', 'example.invalid'] }));
      const hashes = path.join(w.dir, 'hashes.json');
      writeFileSync(hashes, JSON.stringify(privacyHashDocument(w.config.paths.privacy)));
      const policy = loadPrivacyPolicy({ hashFile: hashes, requireHashes: true });
      assert.ok(scanForPrivateTerms(policy, [{ field: 'probe', text }]).length > 0);
      const localPolicy = loadPrivacyPolicy({ localFile: w.config.paths.privacy });
      assert.ok(scanForPrivateTerms(localPolicy, [{ field: 'probe', text }]).length > 0, 'local policy protects separators without public hashes');
      assert.doesNotMatch(readFileSync(hashes, 'utf8'), /Synthetic|probe@example/i);
    } finally { w.cleanup(); }
  });
}

test('v1, wrong normalisation, missing schema refused; identical duplicate allowed, conflicting widths refused', () => {
  const w = workspace();
  try {
    const file = path.join(w.dir, 'hashes.json');
    const document = privacyHashDocument(w.config.paths.privacy);
    for (const change of [{ schema: 'palabras.council.privacy.sha256.v1' }, { schema: undefined }, { normalisation: 'old' }, { algorithm: 'sha1' }]) {
      writeFileSync(file, JSON.stringify({ ...document, ...change }));
      assert.throws(() => loadPrivacyPolicy({ hashFile: file }), PrivacyConfigError);
    }
    writeFileSync(file, JSON.stringify({ ...document, terms: [document.terms[0], document.terms[0]] }));
    assert.equal(loadPrivacyPolicy({ hashFile: file }).hashedTerms.length, 2);
    writeFileSync(file, JSON.stringify({ ...document, terms: [document.terms[0], { ...document.terms[0], tokens: 1 }] }));
    assert.throws(() => loadPrivacyPolicy({ hashFile: file }), /conflicting token counts/);
  } finally { w.cleanup(); }
});

test('malformed local JSON cannot echo source fragments; empty local real run refused', async () => {
  const w = workspace();
  try {
    writeFileSync(w.config.paths.privacy, 'UNSAFE secret source');
    assert.throws(() => loadPrivacyPolicy({ localFile: w.config.paths.privacy }), (error) => !error.message.includes('UNSAFE'));
    writeFileSync(w.config.paths.privacy, JSON.stringify({ terms: [] }));
    const err = [];
    let calls = 0;
    assert.equal(await runCli(['propose', '--privacy', w.config.paths.privacy, '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {}, async () => { calls++; return { text: '{}' }; }), 1);
    assert.equal(calls, 0);
    assert.match(err.join('\n'), /non-empty privacy.local.json/);
  } finally { w.cleanup(); }
});

test('privacy chokepoint blocks fully rendered input on every send', async () => {
  const w = workspace();
  try {
    let calls = 0;
    const provider = createProvider(w.config, w.policy, async () => { calls++; return { text: '{}' }; });
    await assert.rejects(provider.send(MODELS[0], 'Inserted synthetic forbidden sentinel.'), /rendered prompt contains a private term/);
    assert.equal(calls, 0);
  } finally { w.cleanup(); }
});
