// Offline end-to-end evidence. Synthetic fixtures only; no API keys or private names.
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { TOOL_ROOT } from '../dist/config.js';
import { privacyHashDocument } from '../dist/privacy.js';
import { proposal, script, workspace, MODELS } from './helpers.mjs';
const w = workspace();
function cli(args) {
  return spawnSync(process.execPath, [path.join(TOOL_ROOT, 'dist/cli.js'), ...args], { encoding: 'utf8', env: { PATH: process.env.PATH }, cwd: TOOL_ROOT });
}
try {
  const replay = path.join(w.dir, 'replay.json');
  writeFileSync(replay, JSON.stringify(script([proposal('zorzal', 'noun'), proposal('zorzal', 'adjective'), proposal('tender', 'verb')], ['es-zorzal-01', 'es-zorzal-02'])));
  const generated = cli(['propose', '--dry-run', '--replay', replay, '--models', MODELS.join(','), '--out', w.config.paths.outDir]);
  assert.equal(generated.status, 0, generated.stderr);
  assert.equal(existsSync(w.config.paths.outDir), false);
  assert.match(generated.stdout, /key=es-zorzal-01.*pos=adjective/);
  assert.match(generated.stdout, /key=es-zorzal-02.*pos=noun/);
  assert.match(generated.stdout, /reason=exists lemma=tender pos=verb/);
  console.log(generated.stdout.trim());
  console.log('dry_run_writes=0 network_calls=0 (replay adapter)');

  writeFileSync(w.config.paths.privacy, JSON.stringify({ terms: ['Synthetic Example'] }));
  const hashes = path.join(w.dir, 'hashes.json');
  writeFileSync(hashes, JSON.stringify(privacyHashDocument(w.config.paths.privacy)));
  const guide = path.join(w.dir, 'guide.md');
  writeFileSync(guide, 'Synthetic Example.');
  const guarded = cli(['propose', '--dry-run', '--style-guide', guide, '--privacy-hashes', hashes]);
  assert.equal(guarded.status, 1);
  assert.match(guarded.stderr, /contains a private term \(hashed #1\)/);
  assert.doesNotMatch(guarded.stderr + guarded.stdout, /Synthetic Example/i);
  console.log('punctuation_adjacent_hash_probe: exit=1');
  console.log(guarded.stderr.split('\n').find((line) => line.includes('hashed #1')).trim().replace(w.dir, '<probe>'));

  const prompts = path.join(w.dir, 'prompts');
  mkdirSync(prompts);
  writeFileSync(path.join(prompts, 'propose.v1.md'), 'Guide {{STYLE_GUIDE}}');
  writeFileSync(path.join(prompts, 'critique.v1.md'), 'Request {{REQUEST}}');
  writeFileSync(guide, 'Inserted {{REQUEST}} must not expand.');
  const leftover = cli(['propose', '--dry-run', '--prompts', prompts, '--style-guide', guide]);
  assert.equal(leftover.status, 1);
  assert.match(leftover.stderr, /leftover prompt placeholder/);
  console.log('leftover_placeholder_probe: exit=1');
  console.log(leftover.stderr.split('\n').find((line) => line.includes('leftover prompt placeholder')));
} finally { w.cleanup(); }
