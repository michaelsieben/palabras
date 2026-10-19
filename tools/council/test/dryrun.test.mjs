import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { runCli } from '../dist/cli.js';
import { CouncilConfigError, loadConfig, parseEnvFile } from '../dist/config.js';
import { MODELS } from './helpers.mjs';
import { PRIVACY_SCHEMA, PRIVACY_NORMALISATION } from '../dist/privacy.js';

/**
 * The ΔH / design ΔE dry run: `propose --dry-run` prints the exact prompts and the resolved
 * configuration, runs the privacy guard, makes no network call and writes no file. It must work
 * without provider keys, because its whole point is to inspect what a run would send.
 */

function capture() {
  const out = [];
  const err = [];
  return {
    io: { out: (line) => out.push(line), err: (line) => err.push(line) },
    stdout: () => out.join('\n'),
    stderr: () => err.join('\n'),
  };
}

function run(args, env = {}) {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-cli-'));
  try {
    const privacy = path.join(temp, 'privacy.local.json');
    const hashes = path.join(temp, 'privacy.sha256.json');
    writeFileSync(privacy, JSON.stringify({ terms: [] }));
    writeFileSync(hashes, JSON.stringify({ schema: PRIVACY_SCHEMA, algorithm: 'sha256', normalisation: PRIVACY_NORMALISATION, terms: [] }));
    const fixtureArgs = args[0] === 'propose' ? [
      '--privacy', privacy, '--privacy-hashes', hashes,
      '--env-file', path.join(temp, 'missing.env'),
    ] : [];
    const captured = capture();
    const exitCode = runCli([args[0], ...fixtureArgs, ...args.slice(1)], captured.io, env);
    return { exitCode, stdout: captured.stdout(), stderr: captured.stderr() };
  } finally { rmSync(temp, { recursive: true, force: true }); }
}

function withNetworkSpy(body) {
  const original = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => {
    calls += 1;
    throw new Error('the council CLI must not call the network here');
  };
  try {
    body();
  } finally {
    globalThis.fetch = original;
  }
  return calls;
}

test('propose --dry-run prints both prompts and the resolved configuration without keys', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-dry-'));
  try {
    const outDir = path.join(temp, 'bundles');
    let result;
    const fetchCalls = withNetworkSpy(() => {
      result = run(['propose', '--dry-run', '--topic', 'food', '--cefr', 'A2', '--count', '40', '--out', outDir, '--models', MODELS.join(',')]);
    });

    assert.equal(result.exitCode, 0, result.stderr);
    assert.equal(fetchCalls, 0);
    assert.equal(existsSync(outDir), false, 'a dry run must not create the output directory');
    assert.match(result.stdout, /^council propose \(dry run\)$/m);
    assert.match(result.stdout, /request: topic=food cefr=A2 count=40/);
    assert.match(result.stdout, /prompt_version=propose:v1 critique:v1/);
    assert.match(result.stdout, /style_guide=content\/style-guide\.md sha256=[0-9a-f]{64}/);
    assert.match(result.stdout, /excluded_pairs=[1-9][0-9]*/);
    assert.match(result.stdout, /privacy=ok local_terms=0 hashed_terms=0/);
    assert.match(result.stdout, /--- prompts\/propose\.v1\.md ---/);
    assert.match(result.stdout, /--- prompts\/critique\.v1\.md ---/);
    // The prompt body itself is printed, not a summary of it.
    assert.match(result.stdout, /You are a lexicographer of \*\*Peninsular Spanish\*\*/);
    assert.match(result.stdout, /Return exactly one entry per candidate/);
    // No key material can appear: the dry run has none to print.
    assert.doesNotMatch(result.stdout, /API_KEY/);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('a real propose requires a non-empty fixture privacy list', () => {
  const result = run(['propose', '--topic', 'food', '--models', MODELS.join(',')]);
  assert.equal(result.exitCode, 1);
  assert.match(result.stderr, /privacy list not found|non-empty privacy.local.json/);
});

test('bad usage exits 1 with a message, never a stack trace', () => {
  assert.equal(run(['propose', 'food']).exitCode, 1);
  assert.match(run(['propose', 'food']).stderr, /takes no positional arguments/);
  assert.match(run(['propose', '--dry-run', '--bogus', 'x']).stderr, /unknown option --bogus/);
  assert.match(run(['propose', '--dry-run', '--topic']).stderr, /needs a value/);
  assert.match(run(['nonsense']).stderr, /unknown command "nonsense"/);
  assert.equal(run(['help']).exitCode, 0);
  assert.match(run(['help']).stdout, /usage: council <command>/);
});

test('request flags are validated against the schema vocabularies', () => {
  const topic = run(['propose', '--dry-run', '--topic', 'space-travel']);
  assert.equal(topic.exitCode, 1);
  assert.match(topic.stderr, /--topic must be one of household, food/);

  const cefr = run(['propose', '--dry-run', '--cefr', 'Z9']);
  assert.equal(cefr.exitCode, 1);
  assert.match(cefr.stderr, /--cefr must be one of A1, A2/);

  const count = run(['propose', '--dry-run', '--count', '0']);
  assert.equal(count.exitCode, 1);
  assert.match(count.stderr, /--count must be a whole number between 1 and 500/);

  const notNumber = run(['propose', '--dry-run', '--count', 'many']);
  assert.equal(notNumber.exitCode, 1);
});

test('--models overrides the roster and an invalid id is refused', () => {
  const overridden = run(['propose', '--dry-run', '--models', 'anthropic:claude-sonnet-5-5,openrouter:vendor/model']);
  assert.equal(overridden.exitCode, 0, overridden.stderr);
  assert.match(overridden.stdout, /models=2 \[anthropic:claude-sonnet-5-5, openrouter:vendor\/model\]/);

  const invalid = run(['propose', '--dry-run', '--models', 'Claude']);
  assert.equal(invalid.exitCode, 1);
  assert.match(invalid.stderr, /is not a provider:model id/);
});

test('a missing prompt version is a configuration error, not an empty run', () => {
  const result = run(['propose', '--dry-run', '--prompt-version', 'v99']);
  assert.equal(result.exitCode, 1);
  assert.match(result.stderr, /prompt file not found/);
});

test('the env file is layered under real variables and parsed without interpolation', () => {
  const temp = mkdtempSync(path.join(os.tmpdir(), 'palabras-council-env-'));
  try {
    const envFile = path.join(temp, '.env.council');
    writeFileSync(
      envFile,
      ['# comment', '', 'COUNCIL_MODELS=anthropic:a,openai:b', 'COUNCIL_MIN_MODELS=3', 'COUNCIL_TEMPERATURE=0.7', "ANTHROPIC_API_KEY='quoted-key'"].join('\n'),
      'utf8'
    );
    assert.deepEqual(parseEnvFile('A=1\n# x\nB="two"\n'), { A: '1', B: 'two' });

    const config = loadConfig({ env: {}, envFile });
    assert.deepEqual(config.models, ['anthropic:a', 'openai:b']);
    assert.equal(config.minModels, 3);
    assert.equal(config.temperature, 0.7);
    assert.equal(config.env.ANTHROPIC_API_KEY, 'quoted-key');
    assert.equal(config.envFileUsed, envFile);
    assert.equal(config.promptVersion.propose, 'v1');

    const overridden = loadConfig({ env: {}, envFile, models: ['mistral:c'] });
    assert.deepEqual(overridden.models, ['mistral:c']);

    writeFileSync(envFile, 'COUNCIL_MIN_MODELS=99\n', 'utf8');
    assert.throws(() => loadConfig({ env: {}, envFile }), CouncilConfigError);

    writeFileSync(envFile, 'COUNCIL_MODELS=NotAModel\n', 'utf8');
    assert.throws(() => loadConfig({ env: {}, envFile }), /not a provider:model id/);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test('the env file is optional and an unset roster is a warning, not a failure', () => {
  const result = run(['propose', '--dry-run', '--env-file', path.join(os.tmpdir(), 'palabras-missing.env')], {});
  assert.equal(result.exitCode, 0, result.stderr);
  assert.match(result.stdout, /models=0 WARN no model roster configured/);
});
