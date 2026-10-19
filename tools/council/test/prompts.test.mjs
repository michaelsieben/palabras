import assert from 'node:assert/strict';
import { readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { TOOL_ROOT } from '../dist/config.js';
import { renderPrompt } from '../dist/prompts.js';
import { sha256Hex } from '../dist/bundle.js';
import { runCli } from '../dist/cli.js';
import { workspace } from './helpers.mjs';
const values = { REQUEST: 'request', STYLE_GUIDE: 'style', CANDIDATE_SCHEMA: 'schema', EXCLUSION_LIST: 'exclude', CANDIDATES: 'candidates' };

test('renderer recognises exactly five names, rejects unknown and leftover placeholders', () => {
  assert.equal(renderPrompt('{{REQUEST}} {{STYLE_GUIDE}} {{CANDIDATE_SCHEMA}} {{EXCLUSION_LIST}} {{CANDIDATES}}', values), 'request style schema exclude candidates');
  assert.throws(() => renderPrompt('{{UNKNOWN}}', values), /unknown prompt placeholder/);
  assert.throws(() => renderPrompt('{{REQUEST}}', { ...values, REQUEST: '{{STYLE_GUIDE}}' }), /leftover prompt placeholder/);
  assert.throws(() => renderPrompt('{{REQUEST', values), /leftover prompt placeholder/);
  assert.equal(renderPrompt('{{CANDIDATE_SCHEMA}}', { ...values, CANDIDATE_SCHEMA: '{"properties":{"foo":{}}}' }), '{"properties":{"foo":{}}}');
});

test('all versioned prompt files match immutable SHA256SUMS pin', () => {
  const dir = path.join(TOOL_ROOT, 'prompts');
  const pinned = new Map(readFileSync(path.join(dir, 'SHA256SUMS'), 'utf8').trim().split('\n').map((line) => {
    const [hash, file] = line.split(/\s+/);
    return [file, hash];
  }));
  const files = readdirSync(dir).filter((file) => /\.v\d+\.md$/.test(file)).sort();
  assert.deepEqual([...pinned.keys()].sort(), files, 'every new prompt version needs a hash pin');
  for (const file of files) assert.equal(sha256Hex(readFileSync(path.join(dir, file))), pinned.get(file), `${file} changed: add a new version instead`);
  assert.match(readFileSync(path.join(dir, 'critique.v1.md'), 'utf8'), /\{\{REQUEST\}\}/);
});

test('CLI leftover-placeholder prompt fails loudly without printing prompt contents', async () => {
  const w = workspace();
  try {
    const dir = path.join(w.dir, 'prompts');
    mkdirSync(dir);
    writeFileSync(path.join(dir, 'propose.v1.md'), 'Request {{REQUEST}}');
    writeFileSync(path.join(dir, 'critique.v1.md'), 'Critique {{CANDIDATES}}');
    const guide = path.join(w.dir, 'style.md');
    writeFileSync(guide, 'Inserted {{REQUEST}} remains literal.');
    writeFileSync(path.join(dir, 'propose.v1.md'), 'Guide {{STYLE_GUIDE}}');
    const out = [], err = [];
    const code = await runCli(['propose', '--dry-run', '--prompts', dir, '--style-guide', guide], { out: (line) => out.push(line), err: (line) => err.push(line) }, {});
    assert.equal(code, 1);
    assert.match(err.join('\n'), /leftover prompt placeholder/);
    assert.equal(out.length, 0);
  } finally { w.cleanup(); }
});
