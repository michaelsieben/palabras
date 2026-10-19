import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { performance } from 'node:perf_hooks';
import type { CouncilConfig } from './config.js';
import { buildManifest, serialiseCandidates, sha256Hex, writeBundle } from './bundle.js';
import type { CouncilCandidate, CouncilManifest, CouncilManifestModel, CouncilRequest } from './bundle.js';
import { loadProducerLexicon, exclusionList } from './lexicon.js';
import type { CorpusLexicon } from './lexicon.js';
import { lemmaNorm, normaliseProposal, slug } from './normalise.js';
import type { ModelProposal, Proposal } from './normalise.js';
import { cluster } from './cluster.js';
import { merge } from './merge.js';
import { parseCritique, validAlternative } from './critique.js';
import { attributed, capAlternatives, enoughAlternatives, mergeAlternatives } from './alternatives.js';
import { score } from './score.js';
import { lint } from './lint.js';
import { promptValues, readTemplate, renderPrompt, PromptError } from './prompts.js';
import { assertPrivateTextSafe, createProvider, PrivacyBlockedError, ProviderError, REPLAY_WRITE_REFUSED } from './provider.js';
import type { ProviderAdapter } from './provider.js';
import type { PrivacyPolicy } from './privacy.js';
import { validateBundle } from './validate.js';
const require = createRequire(import.meta.url);
const { validate } = require('../../content-lint/validate.js') as { validate: (schema: unknown, data: unknown, root: unknown, at: string, errors: string[]) => void };

export interface Dropped { reason: string; key?: string; lemma?: string; pos?: string; model?: string; proposal_index?: number }
export interface PipelineOptions {
  config: CouncilConfig; request: CouncilRequest; policy: PrivacyPolicy; dryRun?: boolean;
  adapter?: ProviderAdapter; lexicon?: CorpusLexicon; runId?: string; sleep?: (ms: number) => Promise<unknown>;
}
export interface PipelineResult { manifest: CouncilManifest; candidates: CouncilCandidate[]; dropped: Dropped[]; dir?: string }

export function assignKeys(candidates: CouncilCandidate[], lexicon: CorpusLexicon): { candidates: CouncilCandidate[]; dropped: Dropped[] } {
  const occupied = new Set(lexicon.keys);
  const exists = new Set(lexicon.entries.map((word) => JSON.stringify([lemmaNorm(word.lemma), word.pos])));
  const accepted: CouncilCandidate[] = [];
  const dropped: Dropped[] = [];
  const sorted = [...candidates].sort((a, b) => {
    const left = [slug(a.lemma), a.pos, lemmaNorm(a.lemma)].join('\0');
    const right = [slug(b.lemma), b.pos, lemmaNorm(b.lemma)].join('\0');
    return left < right ? -1 : left > right ? 1 : 0;
  });
  for (const candidate of sorted) {
    const base = slug(candidate.lemma);
    let reason: string | undefined;
    if (exists.has(JSON.stringify([lemmaNorm(candidate.lemma), candidate.pos]))) reason = 'exists';
    else if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(base)) reason = 'unkeyable_lemma';
    else {
      for (let ordinal = 1; ordinal <= 99; ordinal++) {
        const key = `es-${base}-${String(ordinal).padStart(2, '0')}`;
        if (!occupied.has(key)) { candidate.key = key; occupied.add(key); break; }
      }
      if (!candidate.key) reason = 'key_space_exhausted';
    }
    if (reason) dropped.push({ reason, lemma: candidate.lemma, pos: candidate.pos });
    else accepted.push(candidate);
  }
  return { candidates: accepted, dropped };
}

export async function runPipeline(options: PipelineOptions): Promise<PipelineResult> {
  const { config, request, policy } = options;
  // Replay output is fabricated: it may be previewed, never written as importable evidence.
  if (!options.dryRun && options.adapter?.replay === true) throw new Error(REPLAY_WRITE_REFUSED);
  if (!options.dryRun && policy.localTerms.length === 0) throw new Error('a real run requires a non-empty privacy.local.json');
  const runId = options.runId ?? randomUUID();
  const createdAt = new Date();
  const models: CouncilManifestModel[] = config.models.map((id) => ({ id, phases: { propose: 'failed', critique: 'failed' }, invalid_count: 0 }));
  const provider = createProvider(config, policy, options.adapter, options.sleep);
  const lexicon = options.lexicon ?? loadProducerLexicon(config.paths.wordsDir);
  const values = promptValues(config, request, exclusionList(lexicon.entries));
  const schema = JSON.parse(values.CANDIDATE_SCHEMA);
  const proposeTemplate = readTemplate(config, 'propose');
  const critiqueTemplate = readTemplate(config, 'critique');
  const dropped: Dropped[] = [];
  let candidates: CouncilCandidate[] = [];
  let fatal: string | undefined;
  let privacyFailure = false;
  let proposePrompt = '';
  try {
    for (const [field, text] of [
      ['style-guide.md', values.STYLE_GUIDE], ['propose template', proposeTemplate],
      ['critique template', critiqueTemplate], ['exclusion list (lemma|pos)', values.EXCLUSION_LIST],
    ]) assertPrivateTextSafe(policy, text, field);
    proposePrompt = renderPrompt(proposeTemplate, values);
    assertPrivateTextSafe(policy, proposePrompt, 'rendered propose prompt');
    assertPrivateTextSafe(policy, renderPrompt(critiqueTemplate, values), 'rendered critique prompt');
  } catch (error) {
    fatal = error instanceof PromptError || error instanceof PrivacyBlockedError ? error.message : 'prompt preflight failed';
    privacyFailure = error instanceof PrivacyBlockedError;
  }

  async function call<T>(model: CouncilManifestModel, phase: 'propose' | 'critique' | 'topup', prompt: string, parse: (text: string) => T): Promise<T | null> {
    const start = performance.now();
    const statusPhase = phase === 'topup' ? 'critique' : phase;
    try {
      for (let repair = 0; ; repair++) {
        const reply = await provider.send(model.id, repair === 0 ? prompt : `${prompt}\nReturn valid JSON matching the schema. No prose or unknown fields.`);
        model.tokens = { in: (model.tokens?.in ?? 0) + (reply.inputTokens ?? 0), out: (model.tokens?.out ?? 0) + (reply.outputTokens ?? 0) };
        try {
          const parsed = parse(reply.text);
          if (phase !== 'topup') model.phases[statusPhase] = 'ok';
          return parsed;
        } catch (error) {
          if (!(error instanceof ProviderError) || error.kind !== 'invalid' || repair === 1) throw error;
          model.invalid_count = (model.invalid_count ?? 0) + 1;
        }
      }
    } catch (error) {
      if (error instanceof PrivacyBlockedError || error instanceof PromptError) fatal = error.message;
      if (error instanceof PrivacyBlockedError) privacyFailure = true;
      const safe = error instanceof ProviderError ? error : new ProviderError('invalid');
      if (phase !== 'topup') model.phases[statusPhase] = safe.kind === 'timeout' ? 'timeout' : safe.kind === 'invalid' ? 'invalid' : 'failed';
      if (safe.kind === 'invalid') model.invalid_count = (model.invalid_count ?? 0) + 1;
      model.error = `${phase}: ${error instanceof PrivacyBlockedError ? 'privacy blocked (provider output or rendered prompt)' : safe.message}`;
      return null;
    } finally {
      if (phase !== 'topup') model.latency_ms = { ...model.latency_ms, [statusPhase]: (model.latency_ms?.[statusPhase] ?? 0) + Math.round(performance.now() - start) };
    }
  }

  function parseProposals(text: string, model: CouncilManifestModel): ModelProposal[] {
    let data: { candidates?: unknown } | null;
    try { data = JSON.parse(text); } catch { throw new ProviderError('invalid'); }
    if (!Array.isArray(data?.candidates)) throw new ProviderError('invalid');
    const valid: ModelProposal[] = [];
    for (const [index, item] of data.candidates.entries()) {
      const identity = { model: model.id, proposal_index: index + 1 };
      if (index >= (request.count ?? 500)) { dropped.push({ ...identity, reason: 'request_count_limit' }); continue; }
      // Normalisation before schema checks permits POS aliases/article-prefixed nouns.
      let candidate: Proposal;
      try { candidate = normaliseProposal(item); } catch {
        model.invalid_count!++; dropped.push({ ...identity, reason: 'invalid_proposal' }); continue;
      }
      const errors: string[] = [];
      validate(schema, candidate, schema, 'candidate', errors);
      if (errors.length || candidate.alternatives.some((alt) => !validAlternative(alt, schema, candidate.senses.length, lexicon.keys))) {
        model.invalid_count!++; dropped.push({ ...identity, reason: 'invalid_proposal' }); continue;
      }
      valid.push({ model: model.id, candidate });
    }
    return valid;
  }

  const responses = fatal ? [] : await Promise.all(models.map((model) => call(model, 'propose', proposePrompt, (text) => parseProposals(text, model))));
  if (!fatal && models.filter((model) => model.phases.propose === 'ok').length < config.minModels) fatal = 'propose: healthy models below COUNCIL_MIN_MODELS';
  if (!fatal) {
    const merged = cluster(responses.flatMap((response) => response ?? [])).map((group) => merge(group, config.models, models.filter((model) => model.phases.propose === 'ok').length));
    const keyed = assignKeys(merged, lexicon);
    candidates = keyed.candidates;
    dropped.push(...keyed.dropped);
    for (const candidate of candidates) candidate.council.run_id = runId;
    const knownKeys = new Set([...lexicon.keys, ...candidates.map((candidate) => candidate.key)]);
    // Prebuild/scan ALL batches before any critique call. Model output cannot race a later scan.
    const batches: CouncilCandidate[][] = [];
    for (let start = 0; start < candidates.length; start += 25) batches.push(candidates.slice(start, start + 25));
    let prompts: string[] = [];
    try {
      prompts = batches.map((batch) => renderPrompt(critiqueTemplate, { ...values, CANDIDATES: JSON.stringify(batch) }));
      for (const prompt of prompts) assertPrivateTextSafe(policy, prompt, 'rendered critique prompt');
    }
    catch (error) { fatal = (error as Error).message; privacyFailure = error instanceof PrivacyBlockedError; }
    if (!fatal && batches.length > 0) {
      const critiques = await Promise.all(models.map(async (model) => {
        const results = [];
        for (const [index, batch] of batches.entries()) {
          const entries = await call(model, 'critique', prompts[index], (text) => parseCritique(text, batch, schema, knownKeys));
          if (!entries) return null; // Discard earlier batches: phase success is all-or-nothing per model.
          results.push(...entries);
          if (fatal) return null;
        }
        return { model: model.id, entries: results };
      }));
      if (!fatal && models.filter((model) => model.phases.critique === 'ok').length < config.minModels) fatal = 'critique: healthy models below COUNCIL_MIN_MODELS';
      if (!fatal) {
        for (const response of critiques) {
          if (!response) continue;
          for (const entry of response.entries) {
            const candidate = candidates.find((item) => item.key === entry.key)!;
            candidate.council.critique.push({ model: response.model, verdict: entry.verdict, ...(entry.issues ? { issues: entry.issues } : {}) });
            candidate.alternatives.push(...(entry.suggested_alternatives ?? []).map((alt) => attributed(alt, response.model)));
          }
        }
        const kept = [];
        for (const candidate of candidates) {
          if (!score(candidate)) { dropped.push({ key: candidate.key, lemma: candidate.lemma, pos: candidate.pos, reason: 'low_agreement' }); continue; }
          candidate.alternatives = mergeAlternatives(candidate.alternatives, config.models);
          lint(candidate);
          if (!enoughAlternatives(candidate)) {
            const first = models.find((model) => model.phases.critique === 'ok')!;
            let result = null;
            try {
              const prompt = renderPrompt(critiqueTemplate, { ...values, CANDIDATES: JSON.stringify([candidate]) });
              result = await call(first, 'topup', prompt, (text) => parseCritique(text, [candidate], schema, knownKeys));
            } catch (error) { fatal = (error as Error).message; privacyFailure = error instanceof PrivacyBlockedError; }
            if (result) {
              candidate.alternatives.push(...(result[0].suggested_alternatives ?? []).map((alt) => attributed(alt, first.id)));
              candidate.alternatives = mergeAlternatives(candidate.alternatives, config.models);
              lint(candidate);
            }
          }
          capAlternatives(candidate);
          candidate.council.lint_warnings = [...new Set(candidate.council.lint_warnings)];
          if (!enoughAlternatives(candidate)) dropped.push({ key: candidate.key, lemma: candidate.lemma, pos: candidate.pos, reason: 'insufficient_alternatives' });
          else kept.push(candidate);
          if (fatal) break;
        }
        candidates = kept;
      }
    }
    if (!fatal && !candidates.length) fatal = 'no importable candidates remain';
  }
  // Scan final artefacts too: no dirty derived text can reach committed candidates/dropped.
  try { assertPrivateTextSafe(policy, JSON.stringify({ candidates, dropped }), 'bundle output'); }
  catch (error) { fatal = (error as Error).message; privacyFailure = true; }
  const input = { outDir: config.paths.outDir, runId, mode: 'propose' as const, promptVersion: config.promptVersion,
    styleGuideSha256: sha256Hex(readFileSync(config.paths.styleGuide)), request, models,
    candidates: fatal ? [] : candidates, dropped: privacyFailure ? undefined : dropped, createdAt,
    status: fatal ? 'failed' as const : 'ok' as const, ...(fatal ? { error: fatal } : {}) };
  if (options.dryRun) return { manifest: buildManifest(input, sha256Hex(serialiseCandidates(input.candidates))), candidates: input.candidates, dropped: privacyFailure ? [] : dropped };
  const written = writeBundle(input);
  if (!fatal) {
    const result = validateBundle(written.dir, { knownKeys: lexicon.keys });
    if (!result.valid) {
      // No successful-looking bundle on validation failure. Diagnostics stay field/class only.
      const failed = writeBundle({ ...input, candidates: [], dropped: undefined, status: 'failed', error: 'bundle validation failed', overwrite: true });
      return { manifest: failed.manifest, candidates: [], dropped: [], dir: failed.dir };
    }
  }
  return { manifest: written.manifest, candidates: input.candidates, dropped: privacyFailure ? [] : dropped, dir: written.dir };
}
