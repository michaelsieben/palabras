#!/usr/bin/env node
'use strict';

/**
 * Council bundle linter: contract validation + the Peninsular-Spanish denylist, over every
 * bundle in `content/council/` (or a directory passed on the command line).
 *
 * This is the CI backstop for bundles produced offline by tools/council. It is deliberately
 * separate from the TypeScript CLI validator: the C# importer, the CLI and this script all read
 * the same `content/schema/council-*.schema.json` and the same `tools/council/fixtures/cases.json`,
 * so a producer cannot satisfy one reader and break another.
 *
 * Zero dependencies. Zero network. Zero LLM.
 *
 *   node tools/content-lint/candidates.js                 # lint content/council
 *   node tools/content-lint/candidates.js <dir>           # lint one bundle or a parent of bundles
 *   node tools/content-lint/candidates.js --self-test     # prove every shared fixture verdict
 *
 * Exit 1 on any contract error or when the size guard fails. Denylist hits are warnings: a
 * LatAm form becomes a reviewer chip (`lint_warnings`), it never blocks an import.
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { validate } = require('./validate');
const { containsTerm, fold } = require('./lexicon');

const REPO_ROOT = path.resolve(__dirname, '..', '..');
const SCHEMA_DIR = path.join(REPO_ROOT, 'content', 'schema');
const DEFAULT_COUNCIL_DIR = path.join(REPO_ROOT, 'content', 'council');
const FIXTURE_DIR = path.join(REPO_ROOT, 'tools', 'council', 'fixtures');
const LEXICON_PATH = path.join(REPO_ROOT, 'content', 'style-guide.lexicon.json');
const WORDS_DIR = path.join(REPO_ROOT, 'content', 'words');

// Plan §4.3: ship the data, not a model cache. A 40-word bundle is ~60 KB.
const MAX_CANDIDATES_BYTES = 2 * 1024 * 1024;
const MAX_COUNCIL_BYTES = 10 * 1024 * 1024;

const manifestSchema = JSON.parse(
  fs.readFileSync(path.join(SCHEMA_DIR, 'council-manifest.schema.json'), 'utf8')
);
const candidateSchema = JSON.parse(
  fs.readFileSync(path.join(SCHEMA_DIR, 'council-candidate.schema.json'), 'utf8')
);

function sha256Hex(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

/** Keys already known to the corpus, so a rework target_key can point at a shipped word. */
function loadCorpusKeys() {
  if (!fs.existsSync(WORDS_DIR)) return new Set();
  const keys = new Set();
  for (const file of fs.readdirSync(WORDS_DIR)) {
    if (!file.endsWith('.json')) continue;
    try {
      const data = readJson(path.join(WORDS_DIR, file));
      if (data && typeof data.id === 'string') keys.add(data.id);
    } catch {
      // A broken corpus file is index.js's failure to report, not this linter's.
    }
  }
  return keys;
}

function bandFor(agreement) {
  if (agreement >= 0.75) return 'alta';
  if (agreement >= 0.5) return 'media';
  return null;
}

function addSchemaIssues(schema, data, location, issues) {
  const errors = [];
  validate(schema, data, schema, location, errors);
  for (const message of errors) {
    issues.push({ code: 'schema', location, message });
  }
  return errors.length > 0;
}

function lintManifestSemantics(manifest, issues) {
  const at = 'manifest.json';
  const models = Array.isArray(manifest.models) ? manifest.models : [];
  const ids = new Set();
  for (const model of models) {
    if (ids.has(model.id)) {
      issues.push({ code: 'manifest_semantics', location: at, message: `models[].id '${model.id}' is declared more than once` });
    }
    ids.add(model.id);
  }
  if (manifest.models_requested !== models.length) {
    issues.push({
      code: 'manifest_semantics',
      location: at,
      message: `models_requested is ${manifest.models_requested} but models[] lists ${models.length}; a failed model is still listed`,
    });
  }
  const proposeOk = models.filter((m) => m.phases.propose === 'ok').length;
  const critiqueOk = models.filter((m) => m.phases.critique === 'ok').length;
  if (manifest.models_succeeded.propose !== proposeOk) {
    issues.push({
      code: 'manifest_semantics',
      location: at,
      message: `models_succeeded.propose is ${manifest.models_succeeded.propose} but ${proposeOk} models report phases.propose "ok"`,
    });
  }
  if (manifest.models_succeeded.critique !== critiqueOk) {
    issues.push({
      code: 'manifest_semantics',
      location: at,
      message: `models_succeeded.critique is ${manifest.models_succeeded.critique} but ${critiqueOk} models report phases.critique "ok"`,
    });
  }
  const reduced = proposeOk < manifest.models_requested || critiqueOk < manifest.models_requested;
  if (manifest.reduced_confidence !== reduced) {
    issues.push({
      code: 'manifest_semantics',
      location: at,
      message: `reduced_confidence is ${manifest.reduced_confidence} but propose ok=${proposeOk}/${manifest.models_requested} and critique ok=${critiqueOk}/${manifest.models_requested}`,
    });
  }
}

/** Design ΔE step 9: the denylist judges primary values and every alternative. Warnings only. */
function lintCandidateText(candidate, lexicon, warnings) {
  const denylist = lexicon.denylist;
  const allowFolded = new Set(lexicon.allowlist.map((entry) => fold(entry.term)));
  const exempt = new Set(
    Array.isArray(candidate.lint && candidate.lint.exemptTerms)
      ? candidate.lint.exemptTerms.map(fold)
      : []
  );
  const headwordAllowlisted = allowFolded.has(fold(candidate.lemma));

  function scan(text, where, matchIn) {
    if (typeof text !== 'string') return;
    for (const rule of denylist) {
      if (typeof rule.term !== 'string') continue;
      if (exempt.has(fold(rule.term))) continue;
      const scope = Array.isArray(rule.matchIn) ? rule.matchIn : ['headword', 'example'];
      if (!scope.includes(matchIn)) continue;
      if (!containsTerm(text, rule.term)) continue;
      if (matchIn === 'headword' && headwordAllowlisted) continue;
      warnings.push(`${candidate.key}: ${where} uses "${rule.term}" — Peninsular form: ${rule.peninsular} (${rule.reason})`);
    }
  }

  scan(candidate.lemma, 'lemma', 'headword');
  for (const [index, sense] of (candidate.senses || []).entries()) {
    scan(sense.example_es, `senses[${index}].example_es`, 'example');
  }
  for (const [index, alternative] of (candidate.alternatives || []).entries()) {
    scan(alternative.es, `alternatives[${index}].es`, 'alternative');
  }
}

function validateBundle(dir, context) {
  const issues = [];
  const warnings = [];
  const bundle = path.basename(dir);
  const manifestPath = path.join(dir, 'manifest.json');
  const candidatesPath = path.join(dir, 'candidates.jsonl');

  if (!fs.existsSync(manifestPath)) {
    issues.push({
      code: 'manifest_missing',
      location: 'manifest.json',
      message: 'manifest.json is missing; a bundle is a directory with manifest.json and candidates.jsonl',
    });
    return { bundle, runId: null, candidates: 0, issues, warnings };
  }

  let manifest;
  try {
    manifest = readJson(manifestPath);
  } catch (error) {
    issues.push({ code: 'manifest_schema', location: 'manifest.json', message: `not valid JSON: ${error.message}` });
    return { bundle, runId: null, candidates: 0, issues, warnings };
  }
  if (addSchemaIssues(manifestSchema, manifest, 'manifest.json', issues)) {
    return { bundle, runId: null, candidates: 0, issues, warnings };
  }
  lintManifestSemantics(manifest, issues);
  if (issues.length > 0) {
    return { bundle, runId: manifest.run_id, candidates: 0, issues, warnings };
  }
  if (manifest.status === 'failed') {
    issues.push({
      code: 'failed_status',
      location: 'manifest.json',
      message: 'manifest status is "failed"; the run produced no importable bundle',
    });
    return { bundle, runId: manifest.run_id, candidates: 0, issues, warnings };
  }

  if (!fs.existsSync(candidatesPath)) {
    issues.push({
      code: 'candidates_missing',
      location: 'candidates.jsonl',
      message: 'candidates.jsonl is missing; a status ok bundle must carry candidates',
    });
    return { bundle, runId: manifest.run_id, candidates: 0, issues, warnings };
  }
  const rawBytes = fs.readFileSync(candidatesPath);
  if (rawBytes.length > MAX_CANDIDATES_BYTES) {
    issues.push({
      code: 'bundle_size',
      location: 'candidates.jsonl',
      message: `candidates.jsonl is ${(rawBytes.length / (1024 * 1024)).toFixed(2)} MB; the limit is 2 MB (plan §4.3)`,
    });
  }
  const actualHash = sha256Hex(rawBytes);
  if (manifest.candidates_sha256 !== actualHash) {
    issues.push({
      code: 'candidates_sha256',
      location: 'candidates.jsonl',
      message: `candidates_sha256 ${manifest.candidates_sha256} does not match the file's sha256 ${actualHash}`,
    });
  }

  const lines = rawBytes.toString('utf8').split('\n');
  const keys = new Set();
  const parsed = [];
  let linesWithContent = 0;
  for (const [index, line] of lines.entries()) {
    if (line.trim() === '') continue;
    linesWithContent += 1;
    const at = `candidates.jsonl:${index + 1}`;
    let candidate;
    try {
      candidate = JSON.parse(line);
    } catch (error) {
      issues.push({ code: 'candidate_json', location: at, message: `not valid JSON: ${error.message}` });
      continue;
    }
    if (candidate && candidate.council
      && (!Array.isArray(candidate.council.proposed_by) || candidate.council.proposed_by.length === 0)) {
      issues.push({
        code: 'proposed_by_missing',
        location: at,
        message: 'council.proposed_by is missing or empty; it must list the model ids that proposed this candidate',
      });
      continue;
    }
    if (addSchemaIssues(candidateSchema, candidate, at, issues)) {
      continue;
    }
    if (keys.has(candidate.key)) {
      issues.push({ code: 'duplicate_key', location: at, message: `duplicate key '${candidate.key}'; keys are unique within one bundle` });
      continue;
    }
    keys.add(candidate.key);
    parsed.push({ at, candidate });
  }

  if (manifest.counts.candidates !== linesWithContent) {
    issues.push({
      code: 'counts_mismatch',
      location: 'manifest.json',
      message: `counts.candidates is ${manifest.counts.candidates} but candidates.jsonl has ${linesWithContent} lines`,
    });
  }
  if (linesWithContent === 0) {
    issues.push({
      code: 'zero_candidates',
      location: 'candidates.jsonl',
      message: 'candidates.jsonl holds no candidates; a status ok bundle must import at least one',
    });
    return { bundle, runId: manifest.run_id, candidates: 0, issues, warnings };
  }

  const resolvable = new Set(context.knownKeys);
  for (const key of keys) resolvable.add(key);
  const proposePhase = new Map(manifest.models.map((model) => [model.id, model.phases.propose]));
  const modelIds = new Set(proposePhase.keys());

  for (const { at, candidate } of parsed) {
    const council = candidate.council;
    if (council.run_id !== manifest.run_id) {
      issues.push({
        code: 'run_id_mismatch',
        location: at,
        message: `council.run_id '${council.run_id}' does not match manifest.run_id '${manifest.run_id}'`,
      });
    }
    for (const model of council.proposed_by) {
      if (!proposePhase.has(model)) {
        issues.push({ code: 'model_unknown', location: at, message: `council.proposed_by contains '${model}', which is not declared in manifest.models` });
      } else if (proposePhase.get(model) !== 'ok') {
        issues.push({
          code: 'model_unknown',
          location: at,
          message: `council.proposed_by contains '${model}', whose phases.propose is "${proposePhase.get(model)}"; only a successful proposer can be listed`,
        });
      }
    }
    const expectedBand = bandFor(council.agreement);
    if (expectedBand === null) {
      issues.push({
        code: 'band_agreement',
        location: at,
        message: `council.agreement ${council.agreement} is below 0.50; a candidate that weak belongs in dropped.jsonl`,
      });
    } else if (council.band !== expectedBand) {
      issues.push({
        code: 'band_agreement',
        location: at,
        message: `council.band '${council.band}' disagrees with council.agreement ${council.agreement}: expected '${expectedBand}'`,
      });
    }
    for (const [index, critique] of council.critique.entries()) {
      if (!modelIds.has(critique.model)) {
        issues.push({
          code: 'model_unknown',
          location: at,
          message: `council.critique[${index}].model '${critique.model}' is not declared in manifest.models`,
        });
      }
    }

    const seenAlternatives = new Set();
    for (const [index, alternative] of candidate.alternatives.entries()) {
      const altAt = `${at}.alternatives[${index}]`;
      const senseIndex = alternative.sense === undefined ? 0 : alternative.sense;
      if (senseIndex >= candidate.senses.length) {
        issues.push({
          code: 'dangling_reference',
          location: altAt,
          message: `sense ${senseIndex} does not exist; the candidate has ${candidate.senses.length} sense(s)`,
        });
      }
      for (const model of alternative.models) {
        if (!modelIds.has(model)) {
          issues.push({ code: 'model_unknown', location: altAt, message: `models contains '${model}', which is not declared in manifest.models` });
        }
      }
      if (alternative.support !== alternative.models.length) {
        issues.push({
          code: 'alternative_support',
          location: altAt,
          message: `support is ${alternative.support} but models lists ${alternative.models.length}`,
        });
      }
      if (alternative.target_key !== undefined && !resolvable.has(alternative.target_key)) {
        issues.push({
          code: 'dangling_reference',
          location: altAt,
          message: `target_key '${alternative.target_key}' does not resolve to a word in this bundle or content/words`,
        });
      }
      const identity = `${alternative.field}|${senseIndex}|${alternative.es ?? ''}|${alternative.en ?? ''}`;
      if (seenAlternatives.has(identity)) {
        issues.push({ code: 'duplicate_alternative', location: altAt, message: 'duplicate alternative: same field, sense and value as an earlier entry' });
      }
      seenAlternatives.add(identity);
    }

    for (const [senseIndex] of candidate.senses.entries()) {
      const senseAlternatives = candidate.alternatives.filter(
        (alternative) => (alternative.sense === undefined ? 0 : alternative.sense) === senseIndex
      );
      const glossCount = senseAlternatives.filter((alternative) => alternative.field === 'gloss').length;
      if (glossCount < 2 || glossCount > 4) {
        issues.push({
          code: 'alternative_counts',
          location: at,
          message: `senses[${senseIndex}] has ${glossCount} gloss alternatives; E.3 requires 2-4`,
        });
      }
      const exampleCount = senseAlternatives.filter((alternative) => alternative.field === 'example').length;
      if (exampleCount < 1 || exampleCount > 3) {
        issues.push({
          code: 'alternative_counts',
          location: at,
          message: `senses[${senseIndex}] has ${exampleCount} example alternatives; E.3 requires 1-3`,
        });
      }
      for (const field of ['lemma', 'regional_variant', 'register_variant']) {
        const count = senseAlternatives.filter((alternative) => alternative.field === field).length;
        if (count > 2) {
          issues.push({
            code: 'alternative_counts',
            location: at,
            message: `senses[${senseIndex}] has ${count} ${field} alternatives; E.3 allows at most 2`,
          });
        }
      }
    }

    lintCandidateText(candidate, context.lexicon, warnings);
  }

  return { bundle, runId: manifest.run_id, candidates: parsed.length, issues, warnings };
}

function listBundles(dir) {
  const own = path.join(dir, 'manifest.json');
  if (fs.existsSync(own)) return [dir];
  return fs.readdirSync(dir, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => path.join(dir, entry.name))
    .filter((child) => fs.existsSync(path.join(child, 'manifest.json')))
    .sort();
}

function directorySize(dir) {
  let total = 0;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    total += entry.isDirectory() ? directorySize(full) : fs.statSync(full).size;
  }
  return total;
}

/**
 * Lint every bundle under `dir`. Returns { bundles, issues, warnings, totalBytes }.
 * A missing directory is an error: the image expects `content/council` next to the binary.
 */
function lintCouncilDirectory(dir = DEFAULT_COUNCIL_DIR) {
  const issues = [];
  const warnings = [];
  if (!fs.existsSync(dir)) {
    return {
      bundles: [],
      issues: [{ bundle: path.relative(REPO_ROOT, dir) || dir, code: 'missing_directory', location: dir, message: 'council directory not found; the image and the startup sync expect content/council' }],
      warnings,
      totalBytes: 0,
    };
  }
  const totalBytes = directorySize(dir);
  if (totalBytes > MAX_COUNCIL_BYTES) {
    issues.push({
      bundle: path.relative(REPO_ROOT, dir),
      code: 'bundle_size',
      location: dir,
      message: `content/council is ${(totalBytes / (1024 * 1024)).toFixed(2)} MB; the limit is 10 MB (plan §4.3)`,
    });
  }
  const context = { knownKeys: loadCorpusKeys(), lexicon: readJson(LEXICON_PATH) };
  const bundles = listBundles(dir).map((bundle) => validateBundle(bundle, {
    knownKeys: context.knownKeys,
    lexicon: {
      denylist: Array.isArray(context.lexicon.latamDenylist) ? context.lexicon.latamDenylist : [],
      allowlist: Array.isArray(context.lexicon.peninsularAllowlist) ? context.lexicon.peninsularAllowlist : [],
    },
  }));
  for (const result of bundles) {
    issues.push(...result.issues.map((issue) => ({ bundle: result.bundle, ...issue })));
    warnings.push(...result.warnings);
  }
  return { bundles, issues, warnings, totalBytes };
}

/** Every fixture in tools/council/fixtures must reach the verdict cases.json records. */
function selfTest() {
  const casesPath = path.join(FIXTURE_DIR, 'cases.json');
  const cases = readJson(casesPath).cases;
  const context = { knownKeys: loadCorpusKeys(), lexicon: readJson(LEXICON_PATH) };
  const failures = [];
  for (const entry of cases) {
    const dir = path.join(FIXTURE_DIR, entry.dir);
    if (!fs.existsSync(dir)) {
      failures.push(`${entry.dir}: directory missing`);
      continue;
    }
    const result = validateBundle(dir, {
      knownKeys: context.knownKeys,
      lexicon: {
        denylist: Array.isArray(context.lexicon.latamDenylist) ? context.lexicon.latamDenylist : [],
        allowlist: Array.isArray(context.lexicon.peninsularAllowlist) ? context.lexicon.peninsularAllowlist : [],
      },
    });
    const valid = result.issues.length === 0;
    if (valid !== entry.valid) {
      const detail = result.issues.map((issue) => `${issue.location}: ${issue.message}`).join('; ') || '(none)';
      failures.push(`${entry.dir}: expected ${entry.valid ? 'valid' : 'invalid'}, got ${valid ? 'valid' : 'invalid'} (${detail})`);
    }
  }
  if (failures.length > 0) {
    for (const failure of failures) console.error(`FAIL ${failure}`);
    console.error(`\ncouncil-lint self-test: ${failures.length} of ${cases.length} fixture verdict(s) wrong.`);
    process.exit(1);
  }
  console.log(`council-lint self-test: OK — ${cases.length}/${cases.length} shared fixture verdicts match.`);
}

function main() {
  const args = process.argv.slice(2);
  if (args.includes('--self-test')) {
    selfTest();
    return;
  }
  const dir = args.find((arg) => !arg.startsWith('-')) || DEFAULT_COUNCIL_DIR;
  const result = lintCouncilDirectory(path.resolve(dir));

  for (const warning of result.warnings) console.warn(`WARN ${warning}`);
  for (const issue of result.issues) {
    console.error(`FAIL ${issue.bundle}: ${issue.location}: ${issue.message}`);
  }
  const bundleCount = result.bundles.length;
  if (result.issues.length > 0) {
    console.error(`\ncouncil-lint: ${result.issues.length} error(s), ${result.warnings.length} warning(s) across ${bundleCount} bundle(s).`);
    process.exit(1);
  }
  console.log(`council-lint: OK — ${bundleCount} bundle(s), 0 errors, ${result.warnings.length} warning(s), ${(result.totalBytes / 1024).toFixed(1)} KB.`);
}

if (require.main === module) {
  main();
}

module.exports = { lintCouncilDirectory, validateBundle, selfTest, DEFAULT_COUNCIL_DIR };
