import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { runCli } from '../dist/cli.js';
import { REPO_ROOT } from '../dist/config.js';
import { MODELS, proposal, script, workspace } from './helpers.mjs';

function snapshot(dir) {
  if (!existsSync(dir)) return null;
  return readdirSync(dir, { recursive: true, withFileTypes: true }).map((entry) => {
    const file = path.join(entry.parentPath, entry.name);
    return [path.relative(dir, file), entry.isFile() ? readFileSync(file).toString('hex') : null];
  }).sort(([a], [b]) => a.localeCompare(b));
}

for (const target of ['external', 'existing', 'default']) {
  test(`--replay implies dry-run and writes nothing anywhere (${target} output)`, async () => {
    const w = workspace();
    const committed = path.join(REPO_ROOT, 'content', 'council');
    const replay = path.join(w.dir, 'replay.json');
    writeFileSync(replay, JSON.stringify(script([proposal()], ['es-zorzal-01'])));
    if (target === 'existing') {
      mkdirSync(w.config.paths.outDir);
      writeFileSync(path.join(w.config.paths.outDir, 'keep.txt'), 'unchanged');
    }
    const beforeLocal = snapshot(w.dir);
    const beforeCommitted = snapshot(committed);
    const out = [], err = [];
    const original = globalThis.fetch;
    let networkCalls = 0;
    globalThis.fetch = async () => { networkCalls++; throw new Error('network forbidden'); };
    try {
      const flags = target === 'default' ? [] : ['--out', w.config.paths.outDir];
      // Deliberately omit --dry-run; --out and injected adapter must not override replay safety.
      const code = await runCli(['propose', '--replay', replay, '--privacy', w.config.paths.privacy,
        '--env-file', w.config.envFileUsed ?? path.join(w.dir, 'missing.env'),
        '--models', MODELS.join(','), ...flags], { out: (line) => out.push(line), err: (line) => err.push(line) }, {},
        async () => { throw new Error('injected adapter must not replace replay'); });
      assert.equal(code, 0, err.join('\n'));
      assert.equal(networkCalls, 0);
      assert.match(out.join('\n'), /\(dry run replay\): status=ok candidates=1/);
      assert.doesNotMatch(out.join('\n'), /^bundle=/m);
      assert.deepEqual(snapshot(w.dir), beforeLocal, 'no local file writes');
      assert.deepEqual(snapshot(committed), beforeCommitted, 'no committed bundle writes');
    } finally { globalThis.fetch = original; w.cleanup(); }
  });
}

test('failed replay and attempted write override cannot create a bundle', async () => {
  const w = workspace();
  try {
    const replay = path.join(w.dir, 'replay.json');
    writeFileSync(replay, JSON.stringify(Object.fromEntries(MODELS.map((id) => [id, [{ error: 'auth' }]]))));
    const before = snapshot(w.dir);
    for (const extra of [[], ['--dry-run'], ['--write-replay'], ['--dry-run=false']]) {
      const err = [];
      const code = await runCli(['propose', '--replay', replay, '--privacy', w.config.paths.privacy,
        '--models', MODELS.join(','), '--out', w.config.paths.outDir, ...extra],
        { out() {}, err: (line) => err.push(line) }, {});
      assert.equal(code, 1);
      assert.deepEqual(snapshot(w.dir), before);
    }
  } finally { w.cleanup(); }
});
