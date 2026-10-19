import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { runCli } from '../dist/cli.js';
import { normaliseTerm, termHash, tokenise } from '../dist/privacy.js';

/**
 * T-20, design ΔH: the guard blocks a planted string before any provider call. In C6 no provider
 * exists at all - the dry run is the code path that would send a prompt - so a fetch spy that
 * stays at zero is the strongest available statement. C7 keeps the same guard in front of its
 * adapters; the assertion moves to `run` then, not the guard.
 */

const TOOL_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const REPO_ROOT = path.resolve(TOOL_ROOT, '..', '..');
const STYLE_GUIDE = readFileSync(path.join(REPO_ROOT, 'content', 'style-guide.md'), 'utf8');
const PLANTED = 'María Zubiría';

function capture() {
  const out = [];
  const err = [];
  return {
    io: { out: (line) => out.push(line), err: (line) => err.push(line) },
    stdout: () => out.join('\n'),
    stderr: () => err.join('\n'),
  };
}

function runWithSpy(args) {
  const original = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => {
    calls += 1;
    throw new Error('the privacy guard must abort before any provider call');
  };
  const captured = capture();
  let exitCode;
  try {
    exitCode = runCli(args, captured.io, {});
  } finally {
    globalThis.fetch = original;
  }
  return { exitCode, stdout: captured.stdout(), stderr: captured.stderr(), fetchCalls: calls };
}

function makeWorkspace() {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-privacy-'));
  return {
    temp,
    styleGuideWithPlanted: () => {
      const file = path.join(temp, 'style-guide.md');
      writeFileSync(file, `${STYLE_GUIDE}\n\n## Planted for the test\n\n${PLANTED} lives here.\n`, 'utf8');
      return file;
    },
    styleGuideClean: () => {
      const file = path.join(temp, 'style-guide.md');
      writeFileSync(file, STYLE_GUIDE, 'utf8');
      return file;
    },
    privacyList: (terms) => {
      const file = path.join(temp, 'privacy.local.json');
      writeFileSync(file, JSON.stringify({ schema: 'palabras.council.privacy.local.v1', terms }), 'utf8');
      return file;
    },
  };
}

test('normalisation folds case, accents and whitespace, which is what the hashes are defined on', () => {
  assert.equal(normaliseTerm('  María   Zubiría '), 'maria zubiria');
  assert.deepEqual(tokenise(normaliseTerm('Ana Gómez <ana@example.invalid>')), ['ana', 'gomez', 'ana@example.invalid']);
  assert.equal(termHash('maria zubiria'), termHash(normaliseTerm(PLANTED)));
  assert.equal(termHash('maria zubiria').length, 64);
});

test('a planted name in the style guide aborts the run before any network call, and is never echoed', () => {
  const workspace = makeWorkspace();
  try {
    const result = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      workspace.styleGuideWithPlanted(),
      '--privacy',
      workspace.privacyList(['maria zubiria']),
    ]);

    assert.equal(result.exitCode, 1);
    assert.equal(result.fetchCalls, 0);
    assert.match(result.stderr, /privacy guard blocked the run before any provider call/);
    assert.match(result.stderr, /style-guide\.md contains a private term \(local #1\)/);
    // The diagnostic names the field, never the string: printing it would leak the secret.
    assert.doesNotMatch(result.stderr, /zubir/i);
    assert.doesNotMatch(result.stderr, /mar[ií]a/i);
    // It also never gets as far as printing prompts.
    assert.doesNotMatch(result.stdout, /council propose \(dry run\)/);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});

test('the committed hash-only list blocks the same string without storing it', () => {
  const workspace = makeWorkspace();
  try {
    const normalised = normaliseTerm(PLANTED);
    const hashes = path.join(workspace.temp, 'privacy.sha256.json');
    writeFileSync(
      hashes,
      JSON.stringify({
        schema: 'palabras.council.privacy.sha256.v1',
        algorithm: 'sha256',
        terms: [{ tokens: tokenise(normalised).length, sha256: termHash(normalised) }],
      }),
      'utf8'
    );

    const result = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      workspace.styleGuideWithPlanted(),
      '--privacy-hashes',
      hashes,
    ]);

    assert.equal(result.exitCode, 1);
    assert.equal(result.fetchCalls, 0);
    assert.match(result.stderr, /style-guide\.md contains a private term \(hashed #1\)/);
    assert.doesNotMatch(result.stderr, /zubir/i);
    assert.doesNotMatch(readFileSync(hashes, 'utf8'), /zubir/i);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});

test('the exclusion list and the prompt files are scanned too', () => {
  const workspace = makeWorkspace();
  try {
    const styleGuide = workspace.styleGuideClean();
    const privacy = workspace.privacyList(['maria zubiria']);

    const lexiconDir = path.join(workspace.temp, 'words');
    mkdirSync(lexiconDir);
    writeFileSync(
      path.join(lexiconDir, 'es-plantada-01.json'),
      JSON.stringify({ id: 'es-plantada-01', lemma: PLANTED, pos: 'noun' }),
      'utf8'
    );
    const fromLexicon = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      styleGuide,
      '--privacy',
      privacy,
      '--lexicon',
      lexiconDir,
    ]);
    assert.equal(fromLexicon.exitCode, 1);
    assert.deepEqual(fromLexicon.fetchCalls, 0);
    assert.match(fromLexicon.stderr, /exclusion list \(lemma\|pos\) contains a private term/);

    const promptsDir = path.join(workspace.temp, 'prompts');
    mkdirSync(promptsDir);
    writeFileSync(path.join(promptsDir, 'propose.v1.md'), `Propose as ${PLANTED} would.\n`, 'utf8');
    writeFileSync(path.join(promptsDir, 'critique.v1.md'), readFileSync(path.join(TOOL_ROOT, 'prompts', 'critique.v1.md'), 'utf8'), 'utf8');
    const fromPrompt = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      styleGuide,
      '--privacy',
      privacy,
      '--prompts',
      promptsDir,
    ]);
    assert.equal(fromPrompt.exitCode, 1);
    assert.equal(fromPrompt.fetchCalls, 0);
    assert.match(fromPrompt.stderr, /propose\.v1\.md contains a private term/);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});

test('a clean run passes the guard and reports an empty policy', () => {
  const workspace = makeWorkspace();
  try {
    const result = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      workspace.styleGuideClean(),
      '--privacy',
      workspace.privacyList(['zzqx']),
    ]);
    assert.equal(result.exitCode, 0, result.stderr);
    assert.equal(result.fetchCalls, 0);
    assert.match(result.stdout, /privacy=ok local_terms=1 hashed_terms=0/);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});

test('a configured term that cannot protect anything is refused without echoing it', () => {
  const workspace = makeWorkspace();
  try {
    const result = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      workspace.styleGuideClean(),
      '--privacy',
      workspace.privacyList(['ay']),
    ]);
    assert.equal(result.exitCode, 1);
    assert.match(result.stderr, /shorter than 3 characters after normalisation/);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});

test('an explicitly requested privacy file that is missing is an error, not an empty guard', () => {
  const workspace = makeWorkspace();
  try {
    const result = runWithSpy([
      'propose',
      '--dry-run',
      '--style-guide',
      workspace.styleGuideClean(),
      '--privacy',
      path.join(workspace.temp, 'no-such-privacy.json'),
    ]);
    assert.equal(result.exitCode, 1);
    assert.match(result.stderr, /privacy list not found/);
    assert.equal(result.fetchCalls, 0);
  } finally {
    rmSync(workspace.temp, { recursive: true, force: true });
  }
});
