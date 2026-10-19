import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { REPO_ROOT } from './config.js';
import { CANDIDATES_FILE, MANIFEST_FILE, countCandidateLines, sha256Hex } from './bundle.js';
import type { CouncilAlternative, CouncilCandidate, CouncilManifest } from './bundle.js';

/**
 * Bundle validation: the CLI half of the shared contract in tools/council/fixtures/cases.json.
 *
 * The C# importer (Backend/Palabras/Content/Council/CouncilBundleValidator.cs), the CI backstop
 * (tools/content-lint/candidates.js) and this module are three independent readers of the same
 * two schemas and the same fixtures. They agree on verdicts, not on wording, so a producer cannot
 * satisfy one reader and break another.
 *
 * The JSON-Schema subset is not reimplemented here: it is the zero-dependency validator the repo
 * already ships for content/words (tools/content-lint/validate.js), which is exactly the subset the
 * council schemas are written in.
 */

type SchemaValidator = (schema: unknown, data: unknown, root: unknown, path: string, errors: string[]) => void;

const loadCommonJs = createRequire(import.meta.url);
const { validate: validateSchema } = loadCommonJs('../../content-lint/validate.js') as {
  validate: SchemaValidator;
};

/** Plan §4.3: a bundle is vocabulary, not a model cache. */
export const MAX_CANDIDATES_BYTES = 2 * 1024 * 1024;

export interface BundleIssue {
  /** Stable rule name, matching the `code` a fixture records in cases.json where one exists. */
  code: string;
  location: string;
  message: string;
}

export interface ValidatedCandidate {
  line: number;
  candidate: CouncilCandidate;
}

export interface BundleValidation {
  dir: string;
  runId: string | null;
  manifest: CouncilManifest | null;
  candidates: ValidatedCandidate[];
  issues: BundleIssue[];
  valid: boolean;
}

export interface ValidateOptions {
  /** Keys resolvable outside the bundle (the corpus lexicon). Absent means "caller supplied none". */
  knownKeys?: ReadonlySet<string> | null;
  schemaDir?: string;
  maxCandidatesBytes?: number;
}

interface LoadedSchemas {
  manifest: unknown;
  candidate: unknown;
}

let cachedSchemas: { dir: string; schemas: LoadedSchemas } | null = null;

function schemasFor(schemaDir: string): LoadedSchemas {
  if (cachedSchemas !== null && cachedSchemas.dir === schemaDir) {
    return cachedSchemas.schemas;
  }
  const schemas: LoadedSchemas = {
    manifest: JSON.parse(readFileSync(path.join(schemaDir, 'council-manifest.schema.json'), 'utf8')),
    candidate: JSON.parse(readFileSync(path.join(schemaDir, 'council-candidate.schema.json'), 'utf8')),
  };
  cachedSchemas = { dir: schemaDir, schemas };
  return schemas;
}

function bandFor(agreement: number): 'alta' | 'media' | null {
  if (agreement >= 0.75) return 'alta';
  if (agreement >= 0.5) return 'media';
  return null;
}

/**
 * Schema failures are reported one per underlying error, all at the same bundle location, so a
 * producer sees the whole document's defects in one run. `alternative_field` is separated out
 * because "the chip vocabulary is wrong" is a different producer bug from "the JSON shape is wrong".
 */
function addSchemaIssues(
  schema: unknown,
  data: unknown,
  location: string,
  code: string,
  issues: BundleIssue[]
): boolean {
  const errors: string[] = [];
  validateSchema(schema, data, schema, location, errors);
  for (const message of errors) {
    const isField = message.includes('.alternatives[') && message.includes('.field');
    issues.push({ code: code === 'candidate_schema' && isField ? 'alternative_field' : code, location, message });
  }
  return errors.length > 0;
}

function validateManifestSemantics(manifest: CouncilManifest, issues: BundleIssue[]): void {
  const at = MANIFEST_FILE;
  const ids = new Set<string>();
  for (const model of manifest.models) {
    if (ids.has(model.id)) {
      issues.push({ code: 'manifest_semantics', location: at, message: `models[].id '${model.id}' is declared more than once` });
    }
    ids.add(model.id);
  }
  if (manifest.models_requested !== manifest.models.length) {
    issues.push({
      code: 'manifest_semantics',
      location: at,
      message: `models_requested is ${manifest.models_requested} but models[] lists ${manifest.models.length} entries; a failed model is still listed`,
    });
  }
  const proposeOk = manifest.models.filter((model) => model.phases.propose === 'ok').length;
  const critiqueOk = manifest.models.filter((model) => model.phases.critique === 'ok').length;
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

function validateCandidateSemantics(
  entry: ValidatedCandidate,
  manifest: CouncilManifest,
  resolvableKeys: ReadonlySet<string>,
  hasKnownKeys: boolean,
  issues: BundleIssue[]
): void {
  const at = `${CANDIDATES_FILE}:${entry.line}`;
  const candidate = entry.candidate;
  const proposePhase = new Map(manifest.models.map((model) => [model.id, model.phases.propose]));
  const modelIds = new Set(proposePhase.keys());

  if (candidate.council.run_id !== manifest.run_id) {
    issues.push({
      code: 'run_id_mismatch',
      location: at,
      message: `council.run_id '${candidate.council.run_id}' does not match manifest.run_id '${manifest.run_id}'`,
    });
  }
  for (const model of candidate.council.proposed_by) {
    const phase = proposePhase.get(model);
    if (phase === undefined) {
      issues.push({
        code: 'model_unknown',
        location: at,
        message: `council.proposed_by contains '${model}', which is not declared in manifest.models`,
      });
    } else if (phase !== 'ok') {
      issues.push({
        code: 'model_unknown',
        location: at,
        message: `council.proposed_by contains '${model}', whose phases.propose is "${phase}"; only a model that succeeded in the propose phase can be listed as a proposer`,
      });
    }
  }
  const expectedBand = bandFor(candidate.council.agreement);
  if (expectedBand === null) {
    issues.push({
      code: 'band_agreement',
      location: at,
      message: `council.agreement ${candidate.council.agreement} is below 0.50; a candidate that weak belongs in dropped.jsonl and is never queued`,
    });
  } else if (candidate.council.band !== expectedBand) {
    issues.push({
      code: 'band_agreement',
      location: at,
      message: `council.band '${candidate.council.band}' disagrees with council.agreement ${candidate.council.agreement}: expected '${expectedBand}'`,
    });
  }
  for (const [index, critique] of candidate.council.critique.entries()) {
    if (!modelIds.has(critique.model)) {
      issues.push({
        code: 'model_unknown',
        location: at,
        message: `council.critique[${index}].model '${critique.model}' is not declared in manifest.models`,
      });
    }
  }

  const seenAlternatives = new Set<string>();
  for (const [index, alternative] of candidate.alternatives.entries()) {
    const altAt = `${at}.alternatives[${index}]`;
    const senseIndex = alternative.sense ?? 0;
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
        message: `support is ${alternative.support} but models lists ${alternative.models.length}; support is the number of proposing models`,
      });
    }
    if (alternative.field === 'example' && (alternative.es === undefined || alternative.en === undefined)) {
      issues.push({ code: 'alternative_shape', location: altAt, message: 'an example alternative needs both es and en (E.3 pairs)' });
    }
    if (alternative.field === 'gloss' && alternative.en === undefined) {
      issues.push({ code: 'alternative_shape', location: altAt, message: 'a gloss alternative needs en' });
    }
    if (
      (alternative.field === 'lemma' || alternative.field === 'regional_variant' || alternative.field === 'register_variant') &&
      alternative.es === undefined
    ) {
      issues.push({ code: 'alternative_shape', location: altAt, message: `a ${alternative.field} alternative needs es` });
    }
    if (alternative.target_key !== undefined && !resolvableKeys.has(alternative.target_key)) {
      // Distinguish "this key is unknown" from "the caller never supplied a lexicon": a caller
      // that forgot to wire the corpus would otherwise see a misleading "unknown word".
      issues.push({
        code: 'dangling_reference',
        location: altAt,
        message: hasKnownKeys
          ? `target_key '${alternative.target_key}' does not resolve to a word in this bundle or the known lexicon`
          : `target_key '${alternative.target_key}' does not resolve to a word in this bundle; no known-key set was supplied, so a reference to an existing word cannot be checked`,
      });
    }
    const identity = `${alternative.field}|${senseIndex}|${alternative.es ?? ''}|${alternative.en ?? ''}`;
    if (seenAlternatives.has(identity)) {
      issues.push({ code: 'duplicate_alternative', location: altAt, message: 'duplicate alternative: same field, sense and value as an earlier entry' });
    }
    seenAlternatives.add(identity);
  }

  for (const [senseIndex] of candidate.senses.entries()) {
    const senseAlternatives = candidate.alternatives.filter((alternative) => (alternative.sense ?? 0) === senseIndex);
    const glossCount = senseAlternatives.filter((alternative) => alternative.field === 'gloss').length;
    if (glossCount < 2 || glossCount > 4) {
      issues.push({
        code: 'sense_alternative_counts',
        location: at,
        message: `senses[${senseIndex}] has ${glossCount} gloss alternatives; E.3 requires 2-4`,
      });
    }
    const exampleCount = senseAlternatives.filter((alternative) => alternative.field === 'example').length;
    if (exampleCount < 1 || exampleCount > 3) {
      issues.push({
        code: 'sense_alternative_counts',
        location: at,
        message: `senses[${senseIndex}] has ${exampleCount} example alternatives; E.3 requires 1-3`,
      });
    }
    for (const field of ['lemma', 'regional_variant', 'register_variant'] as const) {
      const count = senseAlternatives.filter((alternative: CouncilAlternative) => alternative.field === field).length;
      if (count > 2) {
        issues.push({
          code: 'sense_alternative_counts',
          location: at,
          message: `senses[${senseIndex}] has ${count} ${field} alternatives; E.3 allows at most 2`,
        });
      }
    }
  }
}

/**
 * Validates one bundle directory. Everything a producer needs to fix is collected; the function
 * never throws for bad data (only for a caller error such as a missing directory).
 */
export function validateBundle(dir: string, options: ValidateOptions = {}): BundleValidation {
  const schemaDir = options.schemaDir ?? path.join(REPO_ROOT, 'content', 'schema');
  const maxBytes = options.maxCandidatesBytes ?? MAX_CANDIDATES_BYTES;
  const issues: BundleIssue[] = [];
  const empty = (manifest: CouncilManifest | null, runId: string | null): BundleValidation => ({
    dir,
    runId,
    manifest,
    candidates: [],
    issues,
    valid: issues.length === 0,
  });

  if (!existsSync(dir)) {
    throw new Error(`bundle directory does not exist: ${dir}`);
  }
  const schemas = schemasFor(schemaDir);

  const manifestPath = path.join(dir, MANIFEST_FILE);
  if (!existsSync(manifestPath)) {
    issues.push({
      code: 'manifest_missing',
      location: MANIFEST_FILE,
      message: 'manifest.json is missing; a bundle is a directory with manifest.json and candidates.jsonl',
    });
    return empty(null, null);
  }
  let manifestNode: unknown;
  try {
    manifestNode = JSON.parse(readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    issues.push({
      code: 'manifest_schema',
      location: MANIFEST_FILE,
      message: `manifest.json is not valid JSON: ${(error as Error).message}`,
    });
    return empty(null, null);
  }
  if (addSchemaIssues(schemas.manifest, manifestNode, MANIFEST_FILE, 'manifest_schema', issues)) {
    return empty(null, null);
  }
  const manifest = manifestNode as CouncilManifest;
  validateManifestSemantics(manifest, issues);
  if (issues.length > 0) {
    // A manifest whose counts or roster contradict themselves cannot be used to judge the
    // candidates: every later rule (run id, model ids, band) reads from it.
    return empty(manifest, manifest.run_id ?? null);
  }
  if (manifest.status === 'failed') {
    issues.push({
      code: 'failed_status',
      location: MANIFEST_FILE,
      message: 'manifest status is "failed"; the run produced no importable bundle',
    });
    return empty(manifest, manifest.run_id);
  }

  const candidatesPath = path.join(dir, CANDIDATES_FILE);
  if (!existsSync(candidatesPath)) {
    issues.push({
      code: 'candidates_missing',
      location: CANDIDATES_FILE,
      message: 'candidates.jsonl is missing; a status ok bundle must carry candidates',
    });
    return empty(manifest, manifest.run_id);
  }
  const rawBytes = readFileSync(candidatesPath);
  if (rawBytes.length > maxBytes) {
    issues.push({
      code: 'bundle_size',
      location: CANDIDATES_FILE,
      message: `candidates.jsonl is ${(rawBytes.length / (1024 * 1024)).toFixed(2)} MB; the limit is ${(maxBytes / (1024 * 1024)).toFixed(0)} MB (plan §4.3)`,
    });
  }
  const actualHash = sha256Hex(rawBytes);
  if (manifest.candidates_sha256 !== actualHash) {
    issues.push({
      code: 'candidates_sha256',
      location: CANDIDATES_FILE,
      message: `candidates_sha256 ${manifest.candidates_sha256} does not match the file's sha256 ${actualHash}`,
    });
  }

  const lines = rawBytes.toString('utf8').split('\n');
  const keys = new Set<string>();
  const parsed: ValidatedCandidate[] = [];
  for (const [index, line] of lines.entries()) {
    if (line.trim() === '') {
      continue;
    }
    const at = `${CANDIDATES_FILE}:${index + 1}`;
    let node: unknown;
    try {
      node = JSON.parse(line);
    } catch (error) {
      issues.push({ code: 'candidate_json', location: at, message: `not valid JSON: ${(error as Error).message}` });
      continue;
    }
    const council = (node as { council?: { proposed_by?: unknown } }).council;
    if (
      council === null ||
      typeof council !== 'object' ||
      !Array.isArray(council.proposed_by) ||
      council.proposed_by.length === 0
    ) {
      // A bundle produced before council.proposed_by existed must fail loudly here rather than
      // import: proposers are not inferable from alternatives, which name dissenters.
      issues.push({
        code: 'proposed_by_missing',
        location: at,
        message:
          'council.proposed_by is missing or empty; it must list the model ids that proposed this candidate, one or more ids that are declared in manifest.models and successful in the propose phase',
      });
      continue;
    }
    if (addSchemaIssues(schemas.candidate, node, at, 'candidate_schema', issues)) {
      continue;
    }
    const candidate = node as CouncilCandidate;
    if (keys.has(candidate.key)) {
      issues.push({
        code: 'duplicate_key',
        location: at,
        message: `duplicate key '${candidate.key}'; keys are unique within one bundle`,
      });
      continue;
    }
    keys.add(candidate.key);
    parsed.push({ line: index + 1, candidate });
  }

  const lineCount = countCandidateLines(rawBytes);
  if (manifest.counts.candidates !== lineCount) {
    issues.push({
      code: 'counts_mismatch',
      location: MANIFEST_FILE,
      message: `counts.candidates is ${manifest.counts.candidates} but candidates.jsonl has ${lineCount} lines`,
    });
  }
  if (lineCount === 0) {
    issues.push({
      code: 'zero_candidates',
      location: CANDIDATES_FILE,
      message: 'candidates.jsonl holds no candidates; a status ok bundle must import at least one',
    });
    return { dir, runId: manifest.run_id, manifest, candidates: [], issues, valid: false };
  }

  const resolvable = new Set<string>(keys);
  if (options.knownKeys !== undefined && options.knownKeys !== null) {
    for (const key of options.knownKeys) {
      resolvable.add(key);
    }
  }
  for (const entry of parsed) {
    validateCandidateSemantics(entry, manifest, resolvable, options.knownKeys !== undefined && options.knownKeys !== null, issues);
  }

  return { dir, runId: manifest.run_id, manifest, candidates: parsed, issues, valid: issues.length === 0 };
}

/** A directory that is itself a bundle, or its direct children that are bundles. */
export function listBundles(target: string): string[] {
  if (!existsSync(target)) {
    return [];
  }
  if (existsSync(path.join(target, MANIFEST_FILE))) {
    return [target];
  }
  return readdirSync(target, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => path.join(target, entry.name))
    .filter((child) => existsSync(path.join(child, MANIFEST_FILE)))
    .sort();
}
