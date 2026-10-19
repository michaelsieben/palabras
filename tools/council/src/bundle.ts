import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';

/**
 * The bundle format: content/council/<run_id>/{manifest.json,candidates.jsonl,dropped.jsonl}.
 *
 * This module owns the writer and the types; `validate.ts` owns the rules. The JSON shapes are
 * pinned by content/schema/council-manifest.schema.json and council-candidate.schema.json, which
 * the C# importer, the CI linter and this CLI all read, so nothing here may invent a field.
 *
 * Determinism matters twice: `candidates_sha256` is the SHA-256 of the raw file bytes (trailing
 * newline included), and the importer hashes a canonical form of each line as the word's
 * content_hash. Both are produced from the same canonical serialiser the backend uses
 * (Backend/Palabras/Content/CanonicalJson.cs): object keys sorted ordinally, array order kept,
 * strings written decoded, no insignificant whitespace.
 */

export const MANIFEST_SCHEMA = 'palabras.council.manifest.v1';
export const MANIFEST_FILE = 'manifest.json';
export const CANDIDATES_FILE = 'candidates.jsonl';
export const DROPPED_FILE = 'dropped.jsonl';

export type CouncilMode = 'propose' | 'rework';
export type PhaseStatus = 'ok' | 'failed' | 'timeout' | 'invalid';
export type CouncilBand = 'alta' | 'media';
export type BundleStatus = 'ok' | 'failed';

export interface CouncilPromptVersions {
  propose: string;
  critique: string;
}

export interface CouncilRequest {
  topic?: string;
  cefr?: string;
  count?: number;
}

export interface CouncilModelPhases {
  propose: PhaseStatus;
  critique: PhaseStatus;
}

export interface CouncilManifestModel {
  id: string;
  phases: CouncilModelPhases;
  latency_ms?: { propose?: number; critique?: number };
  tokens?: { in?: number; out?: number };
  cost_usd_est?: number;
  invalid_count?: number;
  error?: string;
}

export interface CouncilManifest {
  schema: string;
  run_id: string;
  mode: CouncilMode;
  prompt_version: CouncilPromptVersions;
  style_guide_sha256: string;
  request: CouncilRequest;
  models: CouncilManifestModel[];
  models_requested: number;
  models_succeeded: { propose: number; critique: number };
  reduced_confidence: boolean;
  counts: { candidates: number; dropped: number };
  candidates_sha256?: string;
  status: BundleStatus;
  error?: string;
  created_at: string;
}

export interface CouncilSense {
  en_gloss: string;
  en_accepted?: string[];
  es_accepted?: string[];
  example_es: string;
  example_en: string;
  accents_are_the_point?: boolean;
  notes?: string;
}

export interface CouncilAlternative {
  field: 'lemma' | 'gloss' | 'example' | 'regional_note' | 'regional_variant' | 'register_variant';
  sense?: number;
  es?: string;
  en?: string;
  support: number;
  models: string[];
  rationale?: string;
  target_key?: string;
  region?: 'ES' | 'ES-NA';
  register?: 'neutral' | 'colloquial' | 'formal';
}

export interface CouncilCritique {
  model: string;
  verdict: 'ok' | 'issue';
  issues?: string[];
}

export interface CouncilCandidateCouncil {
  run_id: string;
  agreement: number;
  band: CouncilBand;
  proposal_support: number;
  proposed_by: string[];
  critique: CouncilCritique[];
  lint_warnings: string[];
  lint_actions: string[];
}

export interface CouncilCandidate {
  key: string;
  lemma: string;
  article?: 'el' | 'la' | 'los' | 'las';
  gender?: 'masculine' | 'feminine' | 'common' | 'not_applicable';
  pos: string;
  cefr: 'A1' | 'A2' | 'B1' | 'B2' | 'C1' | 'C2';
  topic: string;
  region: 'ES' | 'ES-NA';
  register: string;
  latam_equivalent?: string;
  forms?: { plural?: string };
  senses: CouncilSense[];
  alternatives: CouncilAlternative[];
  council: CouncilCandidateCouncil;
}

/** SHA-256, lowercase hex. The only hasher the CLI uses. */
export function sha256Hex(bytes: Buffer | string): string {
  return createHash('sha256').update(bytes).digest('hex');
}

// Mirrors CanonicalJson.WriteNode: sorted object keys, arrays in order, decoded strings,
// numbers/booleans verbatim. `undefined` members are dropped (JSON has no such value) so a
// caller that leaves an optional field unset does not write a null the schema would refuse.
function canonicalValue(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => canonicalValue(item));
  }
  if (value !== null && typeof value === 'object') {
    const source = value as Record<string, unknown>;
    const sorted: Record<string, unknown> = {};
    for (const key of Object.keys(source).sort()) {
      if (source[key] === undefined) {
        continue;
      }
      sorted[key] = canonicalValue(source[key]);
    }
    return sorted;
  }
  return value;
}

export function canonicalJson(value: unknown): string {
  return JSON.stringify(canonicalValue(value));
}

/** One candidate per line, canonical, terminated by a newline. Empty input writes no bytes. */
export function serialiseCandidates(candidates: CouncilCandidate[]): Buffer {
  if (candidates.length === 0) {
    return Buffer.alloc(0);
  }
  return Buffer.from(candidates.map((candidate) => canonicalJson(candidate)).join('\n') + '\n', 'utf8');
}

export function serialiseJsonLines(records: unknown[]): Buffer {
  if (records.length === 0) {
    return Buffer.alloc(0);
  }
  return Buffer.from(records.map((record) => canonicalJson(record)).join('\n') + '\n', 'utf8');
}

/** Non-empty lines, the same count the C# validator compares with manifest.counts.candidates. */
export function countCandidateLines(bytes: Buffer): number {
  let count = 0;
  for (const line of bytes.toString('utf8').split('\n')) {
    if (line.trim() !== '') {
      count += 1;
    }
  }
  return count;
}

function countPhase(models: CouncilManifestModel[], phase: keyof CouncilModelPhases): number {
  return models.filter((model) => model.phases[phase] === 'ok').length;
}

// Q-M3-11: the manifest cannot claim a confidence the phases do not support. The writer derives
// the three dependent fields instead of trusting a producer, so a hand-written manifest cannot
// disagree with itself.
export function buildManifest(input: BundleWriteInput, candidatesSha256: string | null): CouncilManifest {
  const status = input.status ?? 'ok';
  const models = input.models;
  const proposeOk = countPhase(models, 'propose');
  const critiqueOk = countPhase(models, 'critique');
  const request: CouncilRequest = {};
  if (input.request?.topic !== undefined) request.topic = input.request.topic;
  if (input.request?.cefr !== undefined) request.cefr = input.request.cefr;
  if (input.request?.count !== undefined) request.count = input.request.count;

  const manifest: CouncilManifest = {
    schema: MANIFEST_SCHEMA,
    run_id: input.runId,
    mode: input.mode,
    prompt_version: {
      propose: input.promptVersion.propose,
      critique: input.promptVersion.critique,
    },
    style_guide_sha256: input.styleGuideSha256,
    request,
    models,
    models_requested: models.length,
    models_succeeded: { propose: proposeOk, critique: critiqueOk },
    reduced_confidence: proposeOk < models.length || critiqueOk < models.length,
    counts: { candidates: input.candidates.length, dropped: input.dropped?.length ?? 0 },
    status,
    created_at: (input.createdAt ?? new Date()).toISOString(),
  };
  if (status === 'ok') {
    manifest.candidates_sha256 = candidatesSha256 ?? '';
  }
  if (input.error !== undefined) {
    manifest.error = input.error;
  }
  return manifest;
}

export interface BundleWriteInput {
  /** Parent directory, normally content/council. The bundle is written to <outDir>/<runId>. */
  outDir: string;
  runId: string;
  mode: CouncilMode;
  promptVersion: CouncilPromptVersions;
  styleGuideSha256: string;
  request?: CouncilRequest;
  models: CouncilManifestModel[];
  candidates: CouncilCandidate[];
  dropped?: unknown[];
  createdAt?: Date;
  status?: BundleStatus;
  error?: string;
  /** Refuse by default: a run id identifies one bundle, and a second write would rewrite hash history. */
  overwrite?: boolean;
}

export interface BundleWriteResult {
  dir: string;
  manifest: CouncilManifest;
  candidatesSha256: string | null;
  candidateLines: number;
  candidateBytes: number;
}

const RUN_ID_PATTERN = /^[0-9A-Za-z][0-9A-Za-z._-]*$/;

/**
 * Writes one bundle. Anything refused throws before the directory is touched, and a failed run
 * (status "failed") never gets a candidates.jsonl - the importer and the validator both treat a
 * failed manifest as a non-importable audit record.
 */
export function writeBundle(input: BundleWriteInput): BundleWriteResult {
  if (input.runId.length < 3 || input.runId.length > 64 || !RUN_ID_PATTERN.test(input.runId)) {
    throw new Error(
      `run id "${input.runId}" is not path-safe; it must be 3-64 characters of [0-9A-Za-z._-] and is also the directory name`
    );
  }
  const status = input.status ?? 'ok';
  if (status === 'failed' && input.candidates.length > 0) {
    throw new Error('a failed run carries no importable candidates; write them to dropped.jsonl instead');
  }

  // The run id is not a candidate field to be trusted: stamping it here means a bundle cannot
  // contain a candidate from another run.
  const candidates = input.candidates.map((candidate) => ({
    ...candidate,
    council: { ...candidate.council, run_id: input.runId },
  }));
  const candidateBytes = status === 'ok' ? serialiseCandidates(candidates) : Buffer.alloc(0);
  const candidatesSha256 = status === 'ok' ? sha256Hex(candidateBytes) : null;
  const manifest = buildManifest({ ...input, candidates, status }, candidatesSha256);

  const dir = path.join(input.outDir, input.runId);
  const manifestPath = path.join(dir, MANIFEST_FILE);
  if (existsSync(manifestPath) && input.overwrite !== true) {
    throw new Error(
      `bundle already exists at ${manifestPath}; a run id identifies one bundle (pass overwrite to replace it)`
    );
  }
  mkdirSync(dir, { recursive: true });

  if (status === 'ok') {
    writeFileSync(path.join(dir, CANDIDATES_FILE), candidateBytes);
  } else {
    // A previous ok write of the same run id must not leave importable candidates behind a
    // manifest that now says the run failed.
    rmSync(path.join(dir, CANDIDATES_FILE), { force: true });
  }
  if (input.dropped !== undefined) {
    writeFileSync(path.join(dir, DROPPED_FILE), serialiseJsonLines(input.dropped));
  }
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n', 'utf8');

  return {
    dir,
    manifest,
    candidatesSha256,
    candidateLines: countCandidateLines(candidateBytes),
    candidateBytes: candidateBytes.length,
  };
}
