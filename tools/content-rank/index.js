#!/usr/bin/env node
'use strict';

// The list is an authoring aid only: the importer reads difficulty in word JSON.
// Zero dependencies, network or runtime generation. Check before every import.
const fs = require('node:fs');
const path = require('node:path');
const ROOT = path.resolve(__dirname, '../..');
const BANDS = ['A1', 'A2', 'B1', 'B2', 'C1', 'C2'];

function readRanks(text) {
  const ranks = new Map();
  for (const line of text.split(/\r?\n/u)) {
    const headword = line.trim();
    if (!headword || headword.startsWith('#')) continue;
    if (ranks.has(headword)) throw new Error(`duplicate list headword: ${headword}`);
    const rank = (ranks.size + 1) * 10;
    if (rank > 9999) throw new Error('rank list exceeds schema maximum 9999');
    ranks.set(headword, rank);
  }
  if (ranks.size === 0) throw new Error('rank list is empty');
  return ranks;
}

function rankedSource(source, rank, file) {
  const cefrLines = [...source.matchAll(/^([ \t]*)"cefr"[ \t]*:[ \t]*"[A-C][12]"[ \t]*,[ \t]*(\r?\n)/gmu)];
  if (cefrLines.length !== 1) throw new Error(`${file}: expected exactly one standalone cefr line`);
  const difficultyLines = [...source.matchAll(/^[ \t]*"difficulty"[ \t]*:[^\r\n]*(?:\r?\n|$)/gmu)];
  const data = JSON.parse(source);
  if (difficultyLines.length > 1 || (Object.hasOwn(data, 'difficulty') && difficultyLines.length !== 1)) {
    throw new Error(`${file}: difficulty must occupy exactly one standalone line`);
  }
  if (difficultyLines.length) {
    const property = difficultyLines[0][0].trim().replace(/,$/u, '');
    if (Object.keys(JSON.parse(`{${property}}`)).length !== 1) {
      throw new Error(`${file}: difficulty must occupy exactly one standalone line`);
    }
  }
  let withoutRank = source.replace(/^[ \t]*"difficulty"[ \t]*:[^\r\n]*(?:\r?\n|$)/gmu, '');
  // A rank authored as the last property leaves the preceding comma behind.
  if (difficultyLines.length && !difficultyLines[0][0].trim().endsWith(',')) {
    withoutRank = withoutRank.replace(/,([ \t]*\r?\n[ \t]*\}[ \t]*(?:\r?\n)?$)/u, '$1');
  }
  const match = cefrLines[0];
  const result = withoutRank.replace(match[0], `${match[0]}${match[1]}"difficulty": ${rank},${match[2]}`);
  // Reject formatting that would discard other properties or produce invalid JSON.
  const expected = { ...data, difficulty: rank };
  const actual = JSON.parse(result);
  for (const key of new Set([...Object.keys(expected), ...Object.keys(actual)])) {
    if (JSON.stringify(expected[key]) !== JSON.stringify(actual[key])) {
      throw new Error(`${file}: rank insertion would change ${key}`);
    }
  }
  return result;
}

function run({ wordsDir = path.join(ROOT, 'content/words'), listPath = path.join(ROOT, 'content/frequency/peninsular-a1a2.txt'), write = false } = {}) {
  const ranks = readRanks(fs.readFileSync(listPath, 'utf8'));
  const files = fs.readdirSync(wordsDir).filter(file => file.endsWith('.json')).sort();
  if (files.length === 0) throw new Error('corpus contains no word files');
  const seen = new Set();
  const entries = [];
  const errors = [];
  for (const file of files) {
    const fullPath = path.join(wordsDir, file);
    const source = fs.readFileSync(fullPath, 'utf8');
    const data = JSON.parse(source);
    if (typeof data.headword !== 'string' || !BANDS.includes(data.cefr)) throw new Error(`${file}: invalid headword or CEFR`);
    if (seen.has(data.headword)) errors.push(`duplicate corpus headword: ${data.headword}`);
    seen.add(data.headword);
    const rank = ranks.get(data.headword);
    if (rank === undefined) {
      errors.push(`corpus headword missing from list: ${data.headword}`);
      continue;
    }
    entries.push({ ...data, difficulty: rank, fullPath, source, next: rankedSource(source, rank, file) });
  }
  for (const headword of ranks.keys()) {
    if (!seen.has(headword)) errors.push(`list headword missing from corpus: ${headword}`);
  }
  if (errors.length) throw new Error(errors.join('\n'));
  const changed = entries.filter(entry => entry.source !== entry.next);
  if (!write && changed.length) {
    throw new Error(`${changed.length} word file(s) have missing, stale or misplaced difficulty; run --write`);
  }
  // Validate the entire corpus before touching any file.
  if (write) for (const entry of changed) fs.writeFileSync(entry.fullPath, entry.next);
  return { entries, changed: changed.length };
}

function preview(entries) {
  return BANDS.flatMap(band => {
    const words = entries.filter(entry => entry.cefr === band).sort((a, b) => a.difficulty - b.difficulty);
    if (!words.length) return [];
    const format = word => `${word.difficulty}:${word.headword}`;
    return [`${band}: ${words.length} words`, `  first 10: ${words.slice(0, 10).map(format).join(', ')}`, `  last 10: ${words.slice(-10).map(format).join(', ')}`];
  }).join('\n');
}

function main(args) {
  if (args.some(arg => !['--check', '--write', '--preview'].includes(arg)) || (args.includes('--check') && args.includes('--write'))) {
    throw new Error('usage: node tools/content-rank/index.js [--check | --write] [--preview]');
  }
  const write = args.includes('--write');
  const result = run({ write });
  console.log(`content-rank: OK — ${result.entries.length} word file(s), ${write ? result.changed + ' updated' : 'all ranks match'}.`);
  if (args.includes('--preview')) console.log(preview(result.entries));
}

if (require.main === module) {
  try { main(process.argv.slice(2)); }
  catch (error) { console.error(`content-rank: FAIL — ${error.message}`); process.exitCode = 1; }
}

module.exports = { readRanks, rankedSource, run, preview };
