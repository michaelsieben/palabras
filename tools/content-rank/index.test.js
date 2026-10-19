'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { readRanks, rankedSource, run, preview } = require('./index');
const { validate } = require('../content-lint/validate');
const schema = require('../../content/schema/word.schema.json');

function source(headword, cefr = 'A1', extra = {}) {
  return JSON.stringify({ headword, cefr, senses: [{ gloss: 'unchanged', example: 'No cambia.' }], ...extra }, null, 2) + '\n';
}

function fixture(t, list = '# comment\nagua\n\n hola \n', words = { agua: source('agua'), hola: source('hola') }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'palabras-content-rank-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const wordsDir = path.join(root, 'words');
  fs.mkdirSync(wordsDir);
  const listPath = path.join(root, 'ranks.txt');
  fs.writeFileSync(listPath, list);
  for (const [name, text] of Object.entries(words)) fs.writeFileSync(path.join(wordsDir, name + '.json'), text);
  return { wordsDir, listPath };
}

test('comments and blank lines do not consume ranks; accents remain distinct', () => {
  assert.deepEqual([...readRanks('# comment\r\npapa\r\n\r\npapá\r\n')], [['papa', 10], ['papá', 20]]);
});

test('empty, duplicate and overflowing lists fail', () => {
  assert.throws(() => readRanks('# only comment'), /empty/);
  assert.throws(() => readRanks('agua\nagua'), /duplicate list headword/);
  assert.throws(() => readRanks(Array.from({ length: 1000 }, (_, i) => `word${i}`).join('\n')), /maximum 9999/);
});

test('write inserts one line after cefr, preserves all other bytes, and is idempotent', t => {
  const options = fixture(t);
  const original = fs.readFileSync(path.join(options.wordsDir, 'agua.json'), 'utf8');
  assert.throws(() => run(options), /2 word file\(s\)/);
  assert.equal(fs.readFileSync(path.join(options.wordsDir, 'agua.json'), 'utf8'), original);
  assert.equal(run({ ...options, write: true }).changed, 2);
  const updated = fs.readFileSync(path.join(options.wordsDir, 'agua.json'), 'utf8');
  assert.equal(updated, original.replace('  "cefr": "A1",\n', '  "cefr": "A1",\n  "difficulty": 10,\n'));
  assert.equal(run(options).changed, 0);
  assert.equal(run({ ...options, write: true }).changed, 0);
});

test('CRLF and tabs remain unchanged', () => {
  const original = source('agua').replaceAll('  ', '\t').replaceAll('\n', '\r\n');
  assert.equal(rankedSource(original, 10, 'agua.json'), original.replace('\t"cefr": "A1",\r\n', '\t"cefr": "A1",\r\n\t"difficulty": 10,\r\n'));
});

test('stale and misplaced ranks normalize without changing vocabulary', () => {
  const original = source('agua', 'A1', { difficulty: 90 });
  const updated = rankedSource(original, 20, 'agua.json');
  assert.match(updated, /"cefr": "A1",\n  "difficulty": 20,/);
  assert.deepEqual(JSON.parse(updated), { ...JSON.parse(original), difficulty: 20 });
  assert.equal(rankedSource(updated, 20, 'agua.json'), updated);
});

test('list reordering is detected and write regenerates ranks', t => {
  const options = fixture(t);
  run({ ...options, write: true });
  fs.writeFileSync(options.listPath, 'hola\nagua\n');
  assert.throws(() => run(options), /stale/);
  run({ ...options, write: true });
  assert.equal(JSON.parse(fs.readFileSync(path.join(options.wordsDir, 'hola.json'))).difficulty, 10);
});

test('missing corpus headword fails before any write (including future batches)', t => {
  const options = fixture(t, 'agua\n');
  const original = fs.readFileSync(path.join(options.wordsDir, 'agua.json'), 'utf8');
  assert.throws(() => run({ ...options, write: true }), /corpus headword missing from list: hola/);
  assert.equal(fs.readFileSync(path.join(options.wordsDir, 'agua.json'), 'utf8'), original);
});

test('extra list headword and duplicate corpus headwords fail', t => {
  const extra = fixture(t, 'agua\nhola\nfantasma\n');
  assert.throws(() => run(extra), /list headword missing from corpus: fantasma/);
  const duplicate = fixture(t, 'agua\n', { first: source('agua'), second: source('agua') });
  assert.throws(() => run({ ...duplicate, write: true }), /duplicate corpus headword: agua/);
});

test('empty corpus and invalid JSON fail', t => {
  assert.throws(() => run(fixture(t, 'agua\n', {})), /no word files/);
  assert.throws(() => run(fixture(t, 'agua\n', { agua: '{invalid' })), SyntaxError);
});

test('invalid CEFR fails before writing', t => {
  assert.throws(() => run({ ...fixture(t, 'agua\n', { agua: source('agua', 'Z9') }), write: true }), /invalid headword or CEFR/);
});

test('unexpected layouts and duplicate keys are rejected', () => {
  assert.throws(() => rankedSource('{"headword":"agua","cefr":"A1"}', 10, 'agua.json'), /standalone cefr/);
  const doubleCefr = source('agua').replace('  "cefr": "A1",', '  "cefr": "A1",\n  "cefr": "A1",');
  assert.throws(() => rankedSource(doubleCefr, 10, 'agua.json'), /standalone cefr/);
  const doubleRank = source('agua', 'A1', { difficulty: 20 }).replace('  "difficulty": 20', '  "difficulty": 20,\n  "difficulty": 30');
  assert.throws(() => rankedSource(doubleRank, 10, 'agua.json'), /difficulty must occupy/);
});

test('inline rank with another property cannot discard that property', () => {
  const original = source('agua', 'A1', { difficulty: 20, notes: 'Keep me' }).replace('20,\n  "notes"', '20, "notes"');
  assert.throws(() => rankedSource(original, 10, 'agua.json'), /standalone line/);
});

test('schema keeps difficulty optional but validates integer and bounds', () => {
  assert.equal(schema.required.includes('difficulty'), false);
  for (const value of [1, 10, 5270, 9999]) {
    const errors = [];
    validate(schema.properties.difficulty, value, schema, 'difficulty', errors);
    assert.deepEqual(errors, []);
  }
  for (const value of [0, 10000, 1.5, '10', null]) {
    const errors = [];
    validate(schema.properties.difficulty, value, schema, 'difficulty', errors);
    assert.ok(errors.length > 0, `${value} must fail`);
  }
});

test('preview orders CEFR first then rank, not alphabet or input order', () => {
  const output = preview([{ cefr: 'B1', difficulty: 10, headword: 'aunque' }, { cefr: 'A1', difficulty: 30, headword: 'agua' }, { cefr: 'A1', difficulty: 20, headword: 'hola' }]);
  assert.match(output, /^A1: 2 words\n  first 10: 20:hola, 30:agua/);
  assert.ok(output.indexOf('B1:') > output.indexOf('A1:'));
});

test('CLI rejects unknown or conflicting flags with exit 1', () => {
  for (const args of [['--unknown'], ['--check', '--write']]) {
    const result = spawnSync(process.execPath, [path.join(__dirname, 'index.js'), ...args], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /usage:/);
  }
});

test('shipped corpus has matching integer multiples of ten unique within each CEFR', () => {
  const { entries } = run();
  const seen = new Set();
  for (const entry of entries) {
    const word = JSON.parse(entry.source);
    assert.ok(Number.isInteger(word.difficulty));
    assert.equal(word.difficulty % 10, 0);
    assert.ok(word.difficulty >= 1 && word.difficulty <= 9999);
    const key = `${word.cefr}:${word.difficulty}`;
    assert.equal(seen.has(key), false, key);
    seen.add(key);
  }
});
