import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { sha256Hex, writeBundle } from '../dist/bundle.js';
import { loadCorpusLexicon } from '../dist/lexicon.js';
import { listBundles, validateBundle } from '../dist/validate.js';

/**
 * T-4 (TypeScript half) + T-19's validator half: the shared fixtures in tools/council/fixtures
 * must reach the same accept/reject verdict here as in M3CouncilBundleTests (C#) and in
 * tools/content-lint/candidates.js --self-test. Also proves the writer's output validates and
 * that `council validate` is a read-only, correct-exit-code command.
 */

const TOOL_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const FIXTURES = path.join(TOOL_ROOT, 'fixtures');
const CLI = path.join(TOOL_ROOT, 'dist', 'cli.js');
const REPO_ROOT = path.resolve(TOOL_ROOT, '..', '..');
const CASES = JSON.parse(readFileSync(path.join(FIXTURES, 'cases.json'), 'utf8')).cases;
const KNOWN_KEYS = loadCorpusLexicon(path.join(REPO_ROOT, 'content', 'words')).keys;

function runCli(args) {
  return spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8' });
}

function describe(result) {
  return result.issues.map((issue) => `${issue.location}: ${issue.message}`).join('; ') || '(no issues)';
}

function readFixture(name) {
  const dir = path.join(FIXTURES, name);
  return {
    manifest: JSON.parse(readFileSync(path.join(dir, 'manifest.json'), 'utf8')),
    candidates: readFileSync(path.join(dir, 'candidates.jsonl'), 'utf8')
      .split('\n')
      .filter((line) => line.trim() !== '')
      .map((line) => JSON.parse(line)),
  };
}

test('every shared fixture reaches the verdict cases.json records', () => {
  assert.ok(CASES.length >= 17);
  for (const entry of CASES) {
    const result = validateBundle(path.join(FIXTURES, entry.dir), { knownKeys: KNOWN_KEYS });
    assert.equal(
      result.valid,
      entry.valid,
      `${entry.dir}: expected ${entry.valid ? 'valid' : 'invalid'}, got ${result.valid ? 'valid' : 'invalid'} — ${describe(result)}`
    );
    if (!entry.valid) {
      assert.ok(result.issues.length > 0);
      for (const issue of result.issues) {
        assert.ok(issue.location.length > 0);
        assert.ok(issue.message.length > 0);
        assert.ok(issue.code.length > 0);
      }
    }
  }
});

test('the valid propose fixture exposes the typed manifest and candidates', () => {
  const result = validateBundle(path.join(FIXTURES, 'valid-propose'), { knownKeys: KNOWN_KEYS });
  assert.equal(result.valid, true, describe(result));
  assert.equal(result.runId, '01JAB3KZ8Q4VX2M7Y6T5R4S3N2');
  assert.equal(result.manifest.counts.candidates, 2);
  assert.equal(result.candidates.length, 2);
  assert.equal(result.candidates[0].candidate.key, 'es-patata-01');
  assert.equal(result.candidates[0].candidate.council.band, 'media');
  assert.equal(result.candidates[1].candidate.council.band, 'alta');
});

test('a target_key resolves against a caller-supplied lexicon', () => {
  const unresolved = validateBundle(path.join(FIXTURES, 'dangling-alternative-reference'), { knownKeys: null });
  assert.equal(unresolved.valid, false);
  assert.ok(unresolved.issues.some((issue) => issue.message.includes('no known-key set was supplied')));

  const resolved = validateBundle(path.join(FIXTURES, 'dangling-alternative-reference'), {
    knownKeys: new Set(['es-inexistente-01']),
  });
  assert.equal(resolved.valid, true, describe(resolved));
});

test('validating a bundle is read-only', () => {
  const dir = path.join(FIXTURES, 'valid-propose');
  const before = readdirSync(dir).sort();
  const hashBefore = sha256Hex(readFileSync(path.join(dir, 'candidates.jsonl')));
  for (let run = 0; run < 3; run += 1) {
    validateBundle(dir, { knownKeys: KNOWN_KEYS });
  }
  assert.deepEqual(readdirSync(dir).sort(), before);
  assert.equal(sha256Hex(readFileSync(path.join(dir, 'candidates.jsonl'))), hashBefore);
});

test('the CLI exits 0 on a valid bundle and 1 on every refusal', () => {
  const valid = runCli(['validate', path.join(FIXTURES, 'valid-propose'), path.join(FIXTURES, 'valid-rework')]);
  assert.equal(valid.status, 0, valid.stderr);
  assert.match(valid.stdout, /summary: bundles=2 valid=2 invalid=0/);

  const failed = runCli(['validate', path.join(FIXTURES, 'failed-run')]);
  assert.equal(failed.status, 1);
  assert.match(failed.stderr, /status is "failed"/);

  const mismatched = runCli(['validate', path.join(FIXTURES, 'hash-mismatch')]);
  assert.equal(mismatched.status, 1);
  assert.match(mismatched.stderr, /candidates_sha256/);

  // A parent directory of bundles is one argument, and one bad child fails the run.
  const parent = runCli(['validate', FIXTURES]);
  assert.equal(parent.status, 1);
  assert.match(parent.stdout, /summary: bundles=17 valid=2 invalid=15/);
});

test('the CLI refuses bad usage without touching a database or a bundle', () => {
  assert.equal(runCli([]).status, 1);
  assert.equal(runCli(['nonsense']).status, 1);
  assert.equal(runCli(['validate']).status, 1);
  assert.equal(runCli(['validate', path.join(FIXTURES, 'valid-propose'), '--bogus', 'x']).status, 1);
  assert.equal(runCli(['validate', path.join(FIXTURES, 'does-not-exist')]).status, 1);

  const external = runCli(['validate', path.join(FIXTURES, 'valid-propose'), '--lexicon', path.join(FIXTURES, 'does-not-exist')]);
  assert.equal(external.status, 1);
});

test('--json reports machine-readable verdicts and keeps the summary on stderr', () => {
  const result = runCli(['validate', path.join(FIXTURES, 'valid-propose'), '--json']);
  assert.equal(result.status, 0);
  const parsed = JSON.parse(result.stdout);
  assert.equal(parsed.bundles.length, 1);
  assert.equal(parsed.bundles[0].valid, true);
  assert.equal(parsed.bundles[0].run_id, '01JAB3KZ8Q4VX2M7Y6T5R4S3N2');
  assert.match(result.stderr, /valid=1/);
});

test('listBundles treats a bundle and a parent of bundles the same way', () => {
  assert.deepEqual(listBundles(path.join(FIXTURES, 'valid-propose')), [path.join(FIXTURES, 'valid-propose')]);
  assert.equal(listBundles(FIXTURES).length, CASES.length);
  assert.deepEqual(listBundles(path.join(FIXTURES, 'missing')), []);
});

test('the writer produces a bundle the validator accepts, byte for byte', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-writer-'));
  try {
    const fixture = readFixture('valid-propose');
    const result = writeBundle({
      outDir: path.join(temp, 'bundles'),
      runId: fixture.manifest.run_id,
      mode: 'propose',
      promptVersion: fixture.manifest.prompt_version,
      styleGuideSha256: fixture.manifest.style_guide_sha256,
      request: fixture.manifest.request,
      models: fixture.manifest.models,
      candidates: fixture.candidates,
      createdAt: new Date(fixture.manifest.created_at),
    });

    const bytes = readFileSync(path.join(result.dir, 'candidates.jsonl'));
    assert.equal(bytes.toString('utf8').endsWith('\n'), true);
    assert.equal(bytes.toString('utf8').trimEnd().split('\n').length, 2);
    assert.equal(result.manifest.candidates_sha256, sha256Hex(bytes));
    assert.equal(result.manifest.status, 'ok');
    assert.equal(result.manifest.counts.candidates, 2);
    assert.equal(result.manifest.models_requested, 3);
    assert.equal(result.manifest.models_succeeded.propose, 3);
    assert.equal(result.manifest.reduced_confidence, false);
    assert.equal(result.manifest.created_at, '2026-10-21T18:00:00.000Z');

    const validation = validateBundle(result.dir, { knownKeys: KNOWN_KEYS });
    assert.equal(validation.valid, true, describe(validation));
    assert.deepEqual(
      validation.candidates.map((entry) => entry.candidate.council.run_id),
      [fixture.manifest.run_id, fixture.manifest.run_id]
    );

    // A run id identifies one bundle: a second write is refused rather than silently rewriting
    // the hash the database will be compared against.
    assert.throws(
      () =>
        writeBundle({
          outDir: path.join(temp, 'bundles'),
          runId: fixture.manifest.run_id,
          mode: 'propose',
          promptVersion: fixture.manifest.prompt_version,
          styleGuideSha256: fixture.manifest.style_guide_sha256,
          models: fixture.manifest.models,
          candidates: fixture.candidates,
        }),
      /bundle already exists/
    );
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('the writer stamps the run id on candidates and refuses a cross-run candidate', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-writer-'));
  try {
    const fixture = readFixture('valid-propose');
    const foreign = fixture.candidates.map((candidate) => ({
      ...candidate,
      council: { ...candidate.council, run_id: '01SOMEOTHER0000000000000000' },
    }));
    const result = writeBundle({
      outDir: temp,
      runId: '01WRITTENRUN000000000000000',
      mode: 'propose',
      promptVersion: { propose: 'v1', critique: 'v1' },
      styleGuideSha256: fixture.manifest.style_guide_sha256,
      models: fixture.manifest.models,
      candidates: foreign,
    });
    const written = readFileSync(path.join(result.dir, 'candidates.jsonl'), 'utf8')
      .split('\n')
      .filter((line) => line.trim() !== '')
      .map((line) => JSON.parse(line));
    for (const candidate of written) {
      assert.equal(candidate.council.run_id, '01WRITTENRUN000000000000000');
    }
    const validation = validateBundle(result.dir, { knownKeys: KNOWN_KEYS });
    assert.equal(validation.valid, true, describe(validation));
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('a failed run writes a manifest and no candidates, and validate refuses it', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-writer-'));
  try {
    const fixture = readFixture('valid-propose');
    const result = writeBundle({
      outDir: temp,
      runId: '01FAILEDRUN000000000000000',
      mode: 'propose',
      promptVersion: { propose: 'v1', critique: 'v1' },
      styleGuideSha256: fixture.manifest.style_guide_sha256,
      models: [{ id: 'anthropic:claude-sonnet-5-5', phases: { propose: 'failed', critique: 'failed' }, error: 'auth' }],
      candidates: [],
      status: 'failed',
      error: 'COUNCIL_MIN_MODELS=2 was not met: 0 propose and 0 critique succeeded',
    });

    assert.equal(existsSync(path.join(result.dir, 'candidates.jsonl')), false);
    assert.equal(result.candidatesSha256, null);
    assert.equal(result.manifest.candidates_sha256, undefined);
    assert.equal(result.manifest.status, 'failed');
    assert.match(result.manifest.error, /COUNCIL_MIN_MODELS/);

    const validation = validateBundle(result.dir, { knownKeys: KNOWN_KEYS });
    assert.equal(validation.valid, false);
    assert.deepEqual(validation.issues.map((issue) => issue.code), ['failed_status']);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('an overwrite with a failed run removes the previous candidates file', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-writer-'));
  try {
    const fixture = readFixture('valid-propose');
    const runId = '01OVERWRITE000000000000000';
    writeBundle({
      outDir: temp,
      runId,
      mode: 'propose',
      promptVersion: { propose: 'v1', critique: 'v1' },
      styleGuideSha256: fixture.manifest.style_guide_sha256,
      models: fixture.manifest.models,
      candidates: fixture.candidates,
    });
    const failed = writeBundle({
      outDir: temp,
      runId,
      mode: 'propose',
      promptVersion: { propose: 'v1', critique: 'v1' },
      styleGuideSha256: fixture.manifest.style_guide_sha256,
      models: [{ id: 'anthropic:claude-sonnet-5-5', phases: { propose: 'failed', critique: 'failed' } }],
      candidates: [],
      status: 'failed',
      error: 'COUNCIL_MIN_MODELS was not met on the retry',
      overwrite: true,
    });
    assert.equal(existsSync(path.join(failed.dir, 'candidates.jsonl')), false);
    const validation = validateBundle(failed.dir, { knownKeys: KNOWN_KEYS });
    assert.deepEqual(validation.issues.map((issue) => issue.code), ['failed_status']);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('the writer refuses an invalid run id before creating anything', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-writer-'));
  try {
    for (const runId of ['../escape', 'a', 'has space', '']) {
      assert.throws(
        () =>
          writeBundle({
            outDir: temp,
            runId,
            mode: 'propose',
            promptVersion: { propose: 'v1', critique: 'v1' },
            styleGuideSha256: 'a'.repeat(64),
            models: [],
            candidates: [],
          }),
        /not path-safe/
      );
    }
    assert.deepEqual(readdirSync(temp), []);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});
