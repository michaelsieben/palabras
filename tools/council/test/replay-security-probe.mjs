// Offline CLI acceptance probe: synthetic privacy input only; never echoes exclusion tokens.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { REPO_ROOT } from '../dist/config.js';
import { MODELS, proposal, script, workspace } from './helpers.mjs';

function snapshot(dir) {
  if (!existsSync(dir)) return null;
  return readdirSync(dir, { recursive: true, withFileTypes: true }).map((entry) => {
    const file = path.join(entry.parentPath, entry.name);
    return [path.relative(dir, file), entry.isFile() ? readFileSync(file).toString('hex') : null];
  }).sort(([a], [b]) => a.localeCompare(b));
}

const w = workspace();
try {
  const replay = path.join(w.dir, 'replay.json');
  const guide = path.join(w.dir, 'guide.md');
  writeFileSync(replay, JSON.stringify(script([proposal()], ['es-zorzal-01'])));
  writeFileSync(guide, 'synthetic forbidden sentinel.');
  const common = ['propose', '--privacy', w.config.paths.privacy,
    '--env-file', path.join(w.dir, 'missing.env'), '--models', MODELS.join(',')];
  const cli = (flags) => spawnSync(process.execPath, ['--', path.join(REPO_ROOT, 'tools/council/dist/cli.js'),
    ...common, ...flags], { encoding: 'utf8', env: {} });
  const committed = path.join(REPO_ROOT, 'content/council');
  const beforeLocal = snapshot(w.dir), beforeCommitted = snapshot(committed);
  for (const target of ['external', 'default']) {
    const result = cli(['--replay', replay, ...(target === 'external' ? ['--out', w.config.paths.outDir] : [])]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /\(dry run replay\): status=ok candidates=1/);
    assert.deepEqual(snapshot(w.dir), beforeLocal);
    assert.deepEqual(snapshot(committed), beforeCommitted);
    console.log(`replay target=${target} exit=${result.status} local_writes=0 committed_writes=0`);
    console.log(result.stdout.split('\n')[0]);
  }
  const result = cli(['--style-guide', guide, '--out', w.config.paths.outDir]);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /PrivacyBlockedError: privacy guard blocked/);
  assert.match(result.stderr, /guide\.md contains a private term \(local #1\)/);
  assert.doesNotMatch(result.stderr + result.stdout, /synthetic forbidden sentinel/);
  assert.deepEqual(snapshot(w.dir), beforeLocal);
  assert.deepEqual(snapshot(committed), beforeCommitted);
  console.log(`forced_privacy_failure exit=${result.status} writes=0`);
  console.log(result.stderr.trim());
} finally { w.cleanup(); }
