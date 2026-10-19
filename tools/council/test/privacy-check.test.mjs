import assert from 'node:assert/strict';
import { copyFileSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { runCli } from '../dist/cli.js';
import { PRIVACY_SCHEMA, PRIVACY_NORMALISATION, termHash } from '../dist/privacy.js';
import { workspace, MODELS } from './helpers.mjs';

const empty = { schema: PRIVACY_SCHEMA, algorithm: 'sha256', normalisation: PRIVACY_NORMALISATION, terms: [] };
const entry = { tokens: 1, sha256: termHash('synthetic') };

// Run the actual entrypoint against an isolated repo layout, never replacing the committed policy.
function probe(raw) {
  const w = workspace();
  try {
    const tool = path.join(w.dir, 'tools', 'council');
    mkdirSync(path.join(tool, 'dist'), { recursive: true });
    mkdirSync(path.join(tool, 'prompts'));
    mkdirSync(path.join(w.dir, 'content'));
    writeFileSync(path.join(tool, 'package.json'), JSON.stringify({ type: 'module' }));
    for (const file of ['privacy-check.js', 'privacy.js', 'config.js', 'bundle.js']) {
      copyFileSync(new URL(`../dist/${file}`, import.meta.url), path.join(tool, 'dist', file));
    }
    writeFileSync(path.join(tool, 'privacy.sha256.json'), raw);
    writeFileSync(path.join(tool, 'privacy.local.json'), '{unreadable local file');
    writeFileSync(path.join(w.dir, 'content', 'probe.md'), 'Clean content.');
    const result = spawnSync(process.execPath, [path.join(tool, 'dist', 'privacy-check.js')], { encoding: 'utf8' });
    assert.ifError(result.error);
    return result;
  } finally { w.cleanup(); }
}

test('privacy CI entrypoint accepts empty committed list with explicit runtime requirement notice', () => {
  const result = probe(JSON.stringify(empty));
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, '');
  assert.match(result.stdout, /NOTICE: committed hash list is empty — defence-in-depth only; a real run still requires non-empty privacy\.local\.json/);
  assert.doesNotMatch(result.stdout, /guard OK/);
});

const defects = [
  ['unparsable JSON', '{broken', /not readable JSON/],
  ['missing version', JSON.stringify({ ...empty, schema: undefined }), /unsupported privacy hash format/],
  ['unknown version', JSON.stringify({ ...empty, schema: 'unknown' }), /unsupported privacy hash format/],
  ['plaintext entry', JSON.stringify({ ...empty, terms: ['synthetic plaintext sentinel'] }), /plaintext terms are forbidden/],
  ['plaintext property', JSON.stringify({ ...empty, terms: [{ ...entry, term: 'synthetic plaintext sentinel' }] }), /plaintext terms are forbidden/],
  ['malformed hash', JSON.stringify({ ...empty, terms: [{ ...entry, sha256: 'bad' }] }), /sha256 must be 64 lower-case hex characters/],
  ['conflicting token counts', JSON.stringify({ ...empty, terms: [entry, { ...entry, tokens: 2 }] }), /conflicting token counts for one hash/],
];

for (const [name, raw, diagnostic] of defects) {
  test(`privacy CI entrypoint rejects ${name} with exit 1 and safe diagnostic`, () => {
    const result = probe(raw);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, diagnostic);
    assert.doesNotMatch(result.stderr, /synthetic plaintext sentinel|\{broken/);
    console.log(`privacy CI defect probe: ${name} exit=${result.status}`);
  });
}

test('empty committed list cannot permit a real run without local policy or call a model', async () => {
  const w = workspace();
  try {
    const err = [];
    let calls = 0;
    const code = await runCli(['propose', '--privacy', path.join(w.dir, 'missing-privacy.local.json'),
      '--privacy-hashes', w.config.paths.privacyHashes, '--env-file', path.join(w.dir, 'missing.env'),
      '--models', MODELS.join(',')], { out() {}, err: (line) => err.push(line) }, {},
    async () => { calls++; return { text: '{}' }; });
    assert.equal(code, 1);
    assert.equal(calls, 0);
    assert.match(err.join('\n'), /privacy list not found/);
    console.log(`runtime privacy probe: missing local policy exit=${code} model_calls=${calls}`);
  } finally { w.cleanup(); }
});
