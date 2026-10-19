import { existsSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256Hex } from './bundle.js';

/**
 * Configuration for the offline council CLI.
 *
 * Layering, lowest precedence first: built-in defaults, `.env.council` (gitignored, owner-only),
 * real environment variables, explicit CLI flags. No value here is ever printed with its
 * credential: the provider keys stay inside `env` and are read only by the (C7) provider adapters.
 *
 * Topic and CEFR vocabularies are read from content/schema/council-manifest.schema.json instead
 * of being copied, so a request flag cannot drift from the contract the importer enforces.
 */

// dist/config.js -> tools/council -> tools -> repo root.
const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url));
export const TOOL_ROOT = path.resolve(MODULE_DIR, '..');
export const REPO_ROOT = path.resolve(TOOL_ROOT, '..', '..');

export interface CouncilPaths {
  styleGuide: string;
  schemaDir: string;
  wordsDir: string;
  promptsDir: string;
  outDir: string;
  privacy: string;
  privacyHashes: string;
  envFile: string;
}

export interface CouncilConfig {
  paths: CouncilPaths;
  /** Provider keys and COUNCIL_* values, from the env file and the process environment only. */
  env: Readonly<Record<string, string>>;
  envFileUsed: string | null;
  models: string[];
  minModels: number;
  timeoutSeconds: number;
  temperature: number;
  promptVersion: { propose: string; critique: string };
  topics: string[];
  cefrLevels: string[];
  styleGuideSha256: string;
}

export interface ConfigOverrides {
  env?: Record<string, string | undefined>;
  envFile?: string;
  styleGuide?: string;
  schemaDir?: string;
  wordsDir?: string;
  promptsDir?: string;
  outDir?: string;
  privacy?: string;
  privacyHashes?: string;
  models?: string[];
  minModels?: number;
  timeoutSeconds?: number;
  temperature?: number;
  promptVersion?: string;
}

export class CouncilConfigError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'CouncilConfigError';
  }
}

const MODEL_ID_PATTERN = /^[a-z0-9][a-z0-9-]*:[A-Za-z0-9][A-Za-z0-9._:/-]*$/;
const MAX_MODELS = 8;

/** Minimal KEY=VALUE reader: comments, blank lines, optional quotes, no interpolation, no `export`. */
export function parseEnvFile(text: string): Record<string, string> {
  const values: Record<string, string> = {};
  for (const rawLine of text.split('\n')) {
    const line = rawLine.trim();
    if (line === '' || line.startsWith('#')) {
      continue;
    }
    const separator = line.indexOf('=');
    if (separator <= 0) {
      continue;
    }
    const key = line.slice(0, separator).trim();
    let value = line.slice(separator + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"') && value.length >= 2) ||
      (value.startsWith("'") && value.endsWith("'") && value.length >= 2)
    ) {
      value = value.slice(1, -1);
    }
    values[key] = value;
  }
  return values;
}

function requireInteger(name: string, raw: string, min: number, max: number): number {
  if (!/^\d+$/.test(raw)) {
    throw new CouncilConfigError(`${name} must be a whole number, got "${raw}"`);
  }
  const value = Number.parseInt(raw, 10);
  if (value < min || value > max) {
    throw new CouncilConfigError(`${name} must be between ${min} and ${max}, got ${value}`);
  }
  return value;
}

function requireNumber(name: string, raw: string, min: number, max: number): number {
  const value = Number(raw);
  if (!Number.isFinite(value) || value < min || value > max) {
    throw new CouncilConfigError(`${name} must be a number between ${min} and ${max}, got "${raw}"`);
  }
  return value;
}

function parseModels(raw: string): string[] {
  const models = raw
    .split(',')
    .map((entry) => entry.trim())
    .filter((entry) => entry !== '');
  const seen = new Set<string>();
  for (const model of models) {
    if (!MODEL_ID_PATTERN.test(model)) {
      throw new CouncilConfigError(
        `COUNCIL_MODELS entry "${model}" is not a provider:model id (lower-case provider, e.g. anthropic:claude-sonnet-5-5)`
      );
    }
    if (seen.has(model)) {
      throw new CouncilConfigError(`COUNCIL_MODELS declares "${model}" twice`);
    }
    seen.add(model);
  }
  if (models.length > MAX_MODELS) {
    throw new CouncilConfigError(`COUNCIL_MODELS lists ${models.length} models; the contract allows at most ${MAX_MODELS}`);
  }
  return models;
}

interface ManifestVocabulary {
  topics: string[];
  cefrLevels: string[];
}

function readManifestVocabulary(schemaDir: string): ManifestVocabulary {
  const file = path.join(schemaDir, 'council-manifest.schema.json');
  const schema = JSON.parse(readFileSync(file, 'utf8')) as {
    properties?: { request?: { properties?: { topic?: { enum?: unknown }; cefr?: { enum?: unknown } } } };
  };
  const request = schema.properties?.request?.properties;
  const topics = Array.isArray(request?.topic?.enum) ? request.topic.enum.map(String) : [];
  const cefrLevels = Array.isArray(request?.cefr?.enum) ? request.cefr.enum.map(String) : [];
  if (topics.length === 0 || cefrLevels.length === 0) {
    throw new CouncilConfigError(`${file} does not declare the request.topic and request.cefr vocabularies`);
  }
  return { topics, cefrLevels };
}

/**
 * Highest `<phase>.vN.md` in the prompts directory, so adding a prompt file is the only edit a
 * revision needs (design ΔE: "prompts change by new file, never by edit"). The two phases are
 * discovered independently because the manifest version block is per phase.
 */
export function discoverPromptVersion(promptsDir: string, phase: 'propose' | 'critique'): string | null {
  if (!existsSync(promptsDir)) {
    return null;
  }
  let highest: number | null = null;
  for (const file of readdirSync(promptsDir)) {
    const match = new RegExp(`^${phase}\\.v(\\d+)\\.md$`).exec(file);
    if (!match) {
      continue;
    }
    const version = Number.parseInt(match[1] as string, 10);
    if (highest === null || version > highest) {
      highest = version;
    }
  }
  return highest === null ? null : `v${highest}`;
}

export function loadConfig(overrides: ConfigOverrides = {}): CouncilConfig {
  const processEnv = overrides.env ?? (process.env as Record<string, string | undefined>);
  const envFile = overrides.envFile ?? processEnv.COUNCIL_ENV_FILE ?? path.join(TOOL_ROOT, '.env.council');
  const fileEnv = existsSync(envFile) ? parseEnvFile(readFileSync(envFile, 'utf8')) : {};
  // Keep only what the CLI may need. Copying the whole environment into a configuration object
  // would put unrelated secrets one log statement away from a prompt.
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries({ ...fileEnv, ...processEnv })) {
    if (value !== undefined && (/^COUNCIL_/.test(key) || /_API_KEY$/.test(key))) {
      env[key] = value;
    }
  }

  const paths: CouncilPaths = {
    styleGuide: overrides.styleGuide ?? path.join(REPO_ROOT, 'content', 'style-guide.md'),
    schemaDir: overrides.schemaDir ?? path.join(REPO_ROOT, 'content', 'schema'),
    wordsDir: overrides.wordsDir ?? path.join(REPO_ROOT, 'content', 'words'),
    promptsDir: overrides.promptsDir ?? path.join(TOOL_ROOT, 'prompts'),
    outDir: overrides.outDir ?? path.join(REPO_ROOT, 'content', 'council'),
    privacy: overrides.privacy ?? path.join(TOOL_ROOT, 'privacy.local.json'),
    privacyHashes: overrides.privacyHashes ?? path.join(TOOL_ROOT, 'privacy.sha256.json'),
    envFile,
  };

  if (!existsSync(paths.styleGuide)) {
    throw new CouncilConfigError(`style guide not found: ${paths.styleGuide}`);
  }
  const vocabulary = readManifestVocabulary(paths.schemaDir);

  const models = overrides.models ?? parseModels(env.COUNCIL_MODELS ?? '');
  if (overrides.models !== undefined) {
    // Flags bypass the env parser, so repeat the rules that protect the manifest.
    if (models.length > MAX_MODELS) {
      throw new CouncilConfigError(`--models lists ${models.length} models; the contract allows at most ${MAX_MODELS}`);
    }
    const seen = new Set<string>();
    for (const model of models) {
      if (!MODEL_ID_PATTERN.test(model)) {
        throw new CouncilConfigError(`--models entry "${model}" is not a provider:model id`);
      }
      if (seen.has(model)) {
        throw new CouncilConfigError(`--models declares "${model}" twice`);
      }
      seen.add(model);
    }
  }
  const minModels =
    overrides.minModels ?? (env.COUNCIL_MIN_MODELS === undefined ? 2 : requireInteger('COUNCIL_MIN_MODELS', env.COUNCIL_MIN_MODELS, 1, MAX_MODELS));
  const timeoutSeconds =
    overrides.timeoutSeconds ??
    (env.COUNCIL_TIMEOUT_S === undefined ? 300 : requireInteger('COUNCIL_TIMEOUT_S', env.COUNCIL_TIMEOUT_S, 1, 3600));
  const temperature =
    overrides.temperature ??
    (env.COUNCIL_TEMPERATURE === undefined ? 0.4 : requireNumber('COUNCIL_TEMPERATURE', env.COUNCIL_TEMPERATURE, 0, 2));

  const requestedVersion = overrides.promptVersion;
  const proposeVersion = requestedVersion ?? discoverPromptVersion(paths.promptsDir, 'propose');
  const critiqueVersion = requestedVersion ?? discoverPromptVersion(paths.promptsDir, 'critique');
  if (proposeVersion === null || critiqueVersion === null) {
    throw new CouncilConfigError(
      `no prompt pair found in ${paths.promptsDir}; expected propose.vN.md and critique.vN.md`
    );
  }
  const promptVersion = { propose: proposeVersion, critique: critiqueVersion };
  for (const phase of ['propose', 'critique'] as const) {
    const file = path.join(paths.promptsDir, `${phase}.${promptVersion[phase]}.md`);
    if (!existsSync(file)) {
      throw new CouncilConfigError(`prompt file not found: ${file}`);
    }
  }

  return {
    paths,
    env,
    envFileUsed: existsSync(envFile) ? envFile : null,
    models,
    minModels,
    timeoutSeconds,
    temperature,
    promptVersion,
    topics: vocabulary.topics,
    cefrLevels: vocabulary.cefrLevels,
    styleGuideSha256: sha256Hex(readFileSync(paths.styleGuide)),
  };
}
