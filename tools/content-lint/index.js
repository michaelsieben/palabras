#!/usr/bin/env node
'use strict';

/**
 * content-lint: validates content/words/*.json against content/schema/word.schema.json
 * and applies the Peninsular-Spanish denylist from content/style-guide.lexicon.json.
 *
 * Zero dependencies. Zero network. Zero LLM. Run: node tools/content-lint/index.js
 * Exit code 1 when any error is found, so it can gate CI.
 */

const fs = require('fs');
const path = require('path');
const { validate } = require('./validate');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const SCHEMA_PATH = path.join(REPO_ROOT, 'content', 'schema', 'word.schema.json');
const LEXICON_PATH = path.join(REPO_ROOT, 'content', 'style-guide.lexicon.json');
const WORDS_DIR = path.join(REPO_ROOT, 'content', 'words');

const MAX_EXAMPLE_WORDS = 12;

function loadJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

/** lower-case, accent-sensitive: "papa" stays distinct from "papá". */
function norm(text) {
  return String(text).toLowerCase();
}

/** lower-case, accent-folded ascii: "móvil" -> "movil". Used for allowlist ids. */
function fold(text) {
  return norm(text).normalize('NFD').replace(/[\u0300-\u036f]/g, '');
}

/** Unicode-aware word tokens, punctuation dropped, accents kept. */
function tokens(text) {
  return norm(text)
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean);
}

function containsTerm(text, term) {
  const termTokens = tokens(term);
  if (termTokens.length === 0) return false;
  const textTokens = tokens(text);
  for (let i = 0; i + termTokens.length <= textTokens.length; i += 1) {
    let hit = true;
    for (let j = 0; j < termTokens.length; j += 1) {
      if (textTokens[i + j] !== termTokens[j]) { hit = false; break; }
    }
    if (hit) return true;
  }
  return false;
}

function countWords(text) {
  return tokens(text).length;
}

function main() {
  const errors = [];
  const warnings = [];

  const schema = loadJson(SCHEMA_PATH);
  const lexicon = loadJson(LEXICON_PATH);
  const denylist = Array.isArray(lexicon.latamDenylist) ? lexicon.latamDenylist : [];
  const allowlist = Array.isArray(lexicon.peninsularAllowlist) ? lexicon.peninsularAllowlist : [];

  const allowFolded = new Set(allowlist.map((a) => fold(a.term)));

  if (!fs.existsSync(WORDS_DIR)) {
    console.error(`FAIL content/words directory not found: ${WORDS_DIR}`);
    process.exit(1);
  }

  const files = fs
    .readdirSync(WORDS_DIR)
    .filter((f) => f.endsWith('.json'))
    .sort();

  if (files.length === 0) {
    console.error('FAIL content/words contains no *.json word files');
    process.exit(1);
  }

  const seenIds = new Map();
  const seenSenses = new Map();
  const denyHits = [];

  for (const file of files) {
    const full = path.join(WORDS_DIR, file);

    let data;
    try {
      data = loadJson(full);
    } catch (e) {
      errors.push(`${file}: invalid JSON: ${e.message}`);
      continue;
    }

    // 1. Schema.
    const schemaErrors = [];
    validate(schema, data, schema, file, schemaErrors);
    errors.push(...schemaErrors);

    if (typeof data !== 'object' || data === null || Array.isArray(data)) continue;

    // 2. Filename must be <id>.json.
    if (typeof data.id === 'string' && file !== `${data.id}.json`) {
      errors.push(`${file}: filename must equal id.json (expected ${data.id}.json)`);
    }

    // 3. Unique id.
    if (typeof data.id === 'string') {
      if (seenIds.has(data.id)) errors.push(`${file}: duplicate id "${data.id}" (also in ${seenIds.get(data.id)})`);
      else seenIds.set(data.id, file);
    }

    // 4. Duplicate sense: same headword + same gloss.
    const headword = typeof data.headword === 'string' ? norm(data.headword) : '';
    for (const [i, sense] of (Array.isArray(data.senses) ? data.senses : []).entries()) {
      if (!sense || typeof sense.gloss !== 'string') continue;
      const key = `${headword}|${norm(sense.gloss)}`;
      if (seenSenses.has(key)) {
        errors.push(`${file}: senses[${i}] duplicates sense "${sense.gloss}" already in ${seenSenses.get(key)}`);
      } else {
        seenSenses.set(key, file);
      }
    }

    // 5. Register rule: vulgar needs a note (style-guide.md section 3).
    if (data.register === 'vulgar' && !data.registerNotes) {
      errors.push(`${file}: register "vulgar" requires registerNotes`);
    }

    // 6. Denylist scan.
    const exempt = new Set(
      Array.isArray(data.lint && data.lint.exemptTerms) ? data.lint.exemptTerms.map(fold) : []
    );
    const headwordAllowlisted = typeof data.headword === 'string' && allowFolded.has(fold(data.headword));

    for (const rule of denylist) {
      if (typeof rule.term !== 'string') continue;
      if (exempt.has(fold(rule.term))) continue;
      const matchIn = Array.isArray(rule.matchIn) ? rule.matchIn : ['headword', 'example'];

      if (matchIn.includes('headword') && !headwordAllowlisted && typeof data.headword === 'string') {
        if (containsTerm(data.headword, rule.term)) {
          denyHits.push(`${file}: headword "${data.headword}" uses ${rule.term} — Peninsular form: ${rule.peninsular} (${rule.reason})`);
        }
      }

      if (matchIn.includes('example')) {
        for (const [i, sense] of (Array.isArray(data.senses) ? data.senses : []).entries()) {
          if (sense && typeof sense.example === 'string' && containsTerm(sense.example, rule.term)) {
            denyHits.push(`${file}: senses[${i}].example uses "${rule.term}" — Peninsular form: ${rule.peninsular} (${rule.reason})`);
          }
        }
      }
    }

    // 7. Example sentence rules.
    for (const [i, sense] of (Array.isArray(data.senses) ? data.senses : []).entries()) {
      if (!sense || typeof sense.example !== 'string') continue;
      const example = sense.example;
      const words = countWords(example);
      if (words > MAX_EXAMPLE_WORDS) {
        errors.push(`${file}: senses[${i}].example has ${words} words (max ${MAX_EXAMPLE_WORDS}): "${example}"`);
      }
      const stripped = example.replace(/[")»\s]+$/u, '');
      if (!/[.?!]$/u.test(stripped)) {
        errors.push(`${file}: senses[${i}].example must end with . ? or !: "${example}"`);
      }
      if (example.includes('?') && !example.includes('¿')) {
        errors.push(`${file}: senses[${i}].example has ? without ¿: "${example}"`);
      }
      if (example.includes('!') && !example.includes('¡')) {
        errors.push(`${file}: senses[${i}].example has ! without ¡: "${example}"`);
      }
      if (data.register !== 'formal' && /(^|[^\p{L}])(ustedes|usted)([^\p{L}]|$)/u.test(example)) {
        warnings.push(`${file}: senses[${i}].example uses usted/ustedes but register is "${data.register ?? 'unset'}" (tú/vosotros rule)`);
      }
      if (/(^|[^\p{L}])(vos|voseo)([^\p{L}]|$)/u.test(example)) {
        errors.push(`${file}: senses[${i}].example uses voseo; Peninsular uses tú/vosotros: "${example}"`);
      }
    }
  }

  errors.push(...denyHits);

  for (const w of warnings) console.warn(`WARN ${w}`);
  for (const e of errors) console.error(`FAIL ${e}`);

  const wordCount = files.length;
  if (errors.length > 0) {
    console.error(`\ncontent-lint: ${errors.length} error(s), ${warnings.length} warning(s) across ${wordCount} word file(s).`);
    process.exit(1);
  }
  console.log(`content-lint: OK — ${wordCount} word file(s), 0 errors, ${warnings.length} warning(s).`);
}

main();
