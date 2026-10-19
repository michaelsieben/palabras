import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CouncilConfigError, REPO_ROOT, TOOL_ROOT, loadConfig } from './config.js';
import type { ConfigOverrides, CouncilConfig } from './config.js';
import { exclusionList, loadCorpusLexicon, loadProducerLexicon } from './lexicon.js';
import { PrivacyConfigError, describePrivacyHit, loadPrivacyPolicy, scanForPrivateTerms, privacyHashDocument } from './privacy.js';
import type { PrivacyPolicy, PrivacyScanInput } from './privacy.js';
import { listBundles, validateBundle } from './validate.js';
import type { BundleValidation } from './validate.js';
import { promptValues, renderPrompt } from './prompts.js';
import { runPipeline } from './pipeline.js';
import type { ProviderAdapter } from './provider.js';
import { readReplay } from './providers/replay.js';
import type { CouncilRequest } from './bundle.js';

/**
 * `council` - the offline producer CLI.
 *
 * `validate`, `propose` and `privacy hash`. Dry run previews rendered prompts; an optional
 * replay script exercises the complete pipeline without network calls or bundle writes.
 *
 * Exit codes follow the repo convention (docs/06-content-cli.md): 0 success, 1 anything else.
 * HTTP calls are isolated behind provider.ts's privacy chokepoint. No database access.
 */

export interface CliIo {
  out(line: string): void;
  err(line: string): void;
}

const CONSOLE_IO: CliIo = {
  out: (line) => console.log(line),
  err: (line) => console.error(line),
};

const USAGE = [
  'usage: council <command> [options]',
  '',
  'commands:',
  '  validate <dir>... [--lexicon <dir>] [--json]',
  '      Validate one bundle directory or a parent of bundles against the shared contract',
  '      (content/schema/council-*.schema.json + the semantic rules in tools/council/fixtures).',
  '',
   '  propose [--dry-run] [--topic <topic>] [--cefr <level>] [--count <n>] [--models <list>]',
   '          [--replay <file>] [--style-guide <file>] [--privacy <file>] [--privacy-hashes <file>]',
  '          [--lexicon <dir>] [--prompts <dir>] [--out <dir>] [--prompt-version <vN>]',
  '      Print the exact prompts, the resolved configuration and the exclusion count, and run',
   '      the privacy guard. Dry run: no network or writes; --replay also previews candidates.',
   '  privacy hash [--privacy <file>]',
   '      Print a versioned, sorted, deduplicated hash-only document from the local list.',
  '',
  '  help',
  '      This text.',
].join('\n');

interface ParsedArgs {
  positionals: string[];
  flags: Map<string, string | true>;
}

class UsageError extends Error {}

function parseArgs(args: string[], valueFlags: string[], booleanFlags: string[]): ParsedArgs {
  const positionals: string[] = [];
  const flags = new Map<string, string | true>();
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index] as string;
    if (arg === '--') {
      positionals.push(...args.slice(index + 1));
      break;
    }
    if (!arg.startsWith('--')) {
      positionals.push(arg);
      continue;
    }
    const equals = arg.indexOf('=');
    const name = equals === -1 ? arg : arg.slice(0, equals);
    if (booleanFlags.includes(name)) {
      if (equals !== -1) {
        throw new UsageError(`option ${name} does not take a value`);
      }
      flags.set(name, true);
      continue;
    }
    if (!valueFlags.includes(name)) {
      throw new UsageError(`unknown option ${name}`);
    }
    let value: string | undefined;
    if (equals !== -1) {
      value = arg.slice(equals + 1);
    } else {
      value = args[index + 1];
      index += 1;
    }
    if (value === undefined || value === '' || value.startsWith('--')) {
      throw new UsageError(`option ${name} needs a value`);
    }
    flags.set(name, value);
  }
  return { positionals, flags };
}

function stringFlag(parsed: ParsedArgs, name: string): string | undefined {
  const value = parsed.flags.get(name);
  return typeof value === 'string' ? value : undefined;
}

function displayPath(target: string): string {
  const relative = path.relative(REPO_ROOT, target);
  return relative === '' || relative.startsWith('..') ? target : relative;
}

/** Prompts live under tools/council, outside the repo root's natural display paths. */
function displayToolPath(target: string): string {
  const relative = path.relative(TOOL_ROOT, target);
  return relative === '' || relative.startsWith('..') ? displayPath(target) : relative;
}

function printValidation(io: CliIo, result: BundleValidation): void {
  if (result.valid) {
    io.out(`OK   ${displayPath(result.dir)} run=${result.runId ?? '?'} candidates=${result.candidates.length}`);
    return;
  }
  io.err(`FAIL ${displayPath(result.dir)}${result.runId === null ? '' : ` run=${result.runId}`}`);
  for (const issue of result.issues) {
    io.err(`     ${issue.location}: ${issue.message} [${issue.code}]`);
  }
}

function runValidate(args: string[], io: CliIo): number {
  let parsed: ParsedArgs;
  try {
    parsed = parseArgs(args, ['--lexicon'], ['--json']);
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }
  if (parsed.positionals.length === 0) {
    io.err('Error: council validate needs at least one directory');
    io.err(USAGE);
    return 1;
  }

  const lexiconDir = stringFlag(parsed, '--lexicon') ?? path.join(REPO_ROOT, 'content', 'words');
  let knownKeys: Set<string>;
  try {
    knownKeys = loadCorpusLexicon(lexiconDir).keys;
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }

  const results: BundleValidation[] = [];
  for (const target of parsed.positionals) {
    const resolved = path.resolve(target);
    if (!existsSync(resolved)) {
      io.err(`Error: path does not exist: ${target}`);
      return 1;
    }
    const bundles = listBundles(resolved);
    if (bundles.length === 0) {
      io.err(`Error: no bundle found under ${target}; a bundle is a directory with manifest.json`);
      return 1;
    }
    for (const bundle of bundles) {
      results.push(validateBundle(bundle, { knownKeys }));
    }
  }

  if (parsed.flags.has('--json')) {
    io.out(
      JSON.stringify(
        {
          bundles: results.map((result) => ({
            dir: displayPath(result.dir),
            run_id: result.runId,
            valid: result.valid,
            candidates: result.candidates.length,
            issues: result.issues,
          })),
        },
        null,
        2
      )
    );
  } else {
    for (const result of results) {
      printValidation(io, result);
    }
  }
  const valid = results.filter((result) => result.valid).length;
  const summary = `summary: bundles=${results.length} valid=${valid} invalid=${results.length - valid}`;
  if (parsed.flags.has('--json')) {
    io.err(summary);
  } else {
    io.out(summary);
  }
  return valid === results.length ? 0 : 1;
}

function requireChoice(name: string, value: string, allowed: string[]): string {
  if (!allowed.includes(value)) {
    throw new UsageError(`${name} must be one of ${allowed.join(', ')}, got "${value}"`);
  }
  return value;
}

function readPrompt(promptsDir: string, phase: 'propose' | 'critique', version: string): string {
  const file = path.join(promptsDir, `${phase}.${version}.md`);
  if (!existsSync(file)) {
    throw new Error(`prompt file not found: ${displayPath(file)}`);
  }
  return readFileSync(file, 'utf8');
}

function runPropose(args: string[], io: CliIo, env: Record<string, string | undefined>, adapter?: ProviderAdapter): number | Promise<number> {
  let parsed: ParsedArgs;
  try {
    parsed = parseArgs(
      args,
      [
        '--topic',
        '--cefr',
        '--count',
        '--models',
        '--style-guide',
        '--privacy',
        '--privacy-hashes',
        '--lexicon',
        '--prompts',
        '--out',
        '--env-file',
         '--prompt-version',
         '--replay',
      ],
      ['--dry-run']
    );
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }
  if (parsed.positionals.length > 0) {
    io.err(`Error: council propose takes no positional arguments, got "${parsed.positionals.join(' ')}"`);
    return 1;
  }
  const dryRun = parsed.flags.has('--dry-run');

  const modelsFlag = stringFlag(parsed, '--models');
  const overrides: ConfigOverrides = { env };
  const styleGuideFlag = stringFlag(parsed, '--style-guide');
  if (styleGuideFlag !== undefined) overrides.styleGuide = path.resolve(styleGuideFlag);
  const lexiconFlag = stringFlag(parsed, '--lexicon');
  if (lexiconFlag !== undefined) overrides.wordsDir = path.resolve(lexiconFlag);
  const promptsFlag = stringFlag(parsed, '--prompts');
  if (promptsFlag !== undefined) overrides.promptsDir = path.resolve(promptsFlag);
  const outFlag = stringFlag(parsed, '--out');
  if (outFlag !== undefined) overrides.outDir = path.resolve(outFlag);
  const privacyPathFlag = stringFlag(parsed, '--privacy');
  if (privacyPathFlag !== undefined) overrides.privacy = path.resolve(privacyPathFlag);
  const privacyHashesPathFlag = stringFlag(parsed, '--privacy-hashes');
  if (privacyHashesPathFlag !== undefined) overrides.privacyHashes = path.resolve(privacyHashesPathFlag);
  const envFileFlag = stringFlag(parsed, '--env-file');
  if (envFileFlag !== undefined) overrides.envFile = path.resolve(envFileFlag);
  if (modelsFlag !== undefined) {
    overrides.models = modelsFlag
      .split(',')
      .map((entry) => entry.trim())
      .filter((entry) => entry !== '');
  }
  const promptVersion = stringFlag(parsed, '--prompt-version');
  if (promptVersion !== undefined) {
    overrides.promptVersion = promptVersion;
  }

  let config: CouncilConfig;
  try {
    config = loadConfig(overrides);
  } catch (error) {
    if (error instanceof CouncilConfigError) {
      io.err(`Error: ${error.message}`);
      return 1;
    }
    throw error;
  }

  const topic = stringFlag(parsed, '--topic') ?? null;
  const cefr = stringFlag(parsed, '--cefr') ?? null;
  const countFlag = stringFlag(parsed, '--count');
  const count = countFlag === undefined ? null : Number.parseInt(countFlag, 10);
  try {
    if (topic !== null) {
      requireChoice('--topic', topic, config.topics);
    }
    if (cefr !== null) {
      requireChoice('--cefr', cefr, config.cefrLevels);
    }
    if (count !== null && (!/^\d+$/.test(countFlag as string) || count < 1 || count > 500)) {
      throw new UsageError(`--count must be a whole number between 1 and 500, got "${countFlag}"`);
    }
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }

  let exclusion: string[];
  let lexiconSkipped: number;
  try {
    const lexicon = loadProducerLexicon(config.paths.wordsDir);
    exclusion = exclusionList(lexicon.entries);
    lexiconSkipped = lexicon.skipped;
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }

  const privacyFlag = stringFlag(parsed, '--privacy');
  const privacyHashesFlag = stringFlag(parsed, '--privacy-hashes');
  let policy: PrivacyPolicy;
  try {
    policy = loadPrivacyPolicy({
      localFile: config.paths.privacy,
      hashFile: config.paths.privacyHashes,
      requireLocal: !dryRun || privacyFlag !== undefined,
      requireHashes: privacyHashesFlag !== undefined,
    });
  } catch (error) {
    if (error instanceof PrivacyConfigError) {
      io.err(`Error: ${error.message}`);
      return 1;
    }
    throw error;
  }
  if (!dryRun && policy.localTerms.length === 0) {
    io.err('Error: a real run requires a non-empty privacy.local.json');
    return 1;
  }
  if (policy.localSource !== null && !existsSync(policy.localSource)) {
    io.err(`WARN privacy list not found at ${displayPath(policy.localSource)}; only the committed hash list protects this run`);
  }

  let proposePrompt: string;
  let critiquePrompt: string;
  try {
    proposePrompt = readPrompt(config.paths.promptsDir, 'propose', config.promptVersion.propose);
    critiquePrompt = readPrompt(config.paths.promptsDir, 'critique', config.promptVersion.critique);
  } catch (error) {
    io.err(`Error: ${(error as Error).message}`);
    return 1;
  }

  const prompts: Array<{ field: string; text: string }> = [
    {
      field: displayToolPath(path.join(config.paths.promptsDir, `propose.${config.promptVersion.propose}.md`)),
      text: proposePrompt,
    },
    {
      field: displayToolPath(path.join(config.paths.promptsDir, `critique.${config.promptVersion.critique}.md`)),
      text: critiquePrompt,
    },
  ];
  const scanInputs: PrivacyScanInput[] = [
    { field: displayPath(config.paths.styleGuide), text: readFileSync(config.paths.styleGuide, 'utf8') },
    ...prompts,
    { field: 'exclusion list (lemma|pos)', text: exclusion.join('\n') },
  ];
  const hits = scanForPrivateTerms(policy, scanInputs);
  if (hits.length > 0 && dryRun) {
    io.err('Error: privacy guard blocked the run before any provider call:');
    for (const hit of hits) {
      io.err(`       ${describePrivacyHit(hit)}`);
    }
    return 1;
  }

  const request: CouncilRequest = {};
  if (topic !== null) request.topic = topic;
  if (cefr !== null) request.cefr = cefr;
  if (count !== null) request.count = count;
  try {
    const values = promptValues(config, request, exclusion);
    for (const prompt of prompts) prompt.text = renderPrompt(prompt.text, values);
    const renderedHits = scanForPrivateTerms(policy, prompts);
    if (renderedHits.length) throw new Error(describePrivacyHit(renderedHits[0]));
  } catch (error) {
    if (dryRun) { io.err(`Error: ${(error as Error).message}`); return 1; }
    // A real run records prompt/privacy failure through the pipeline's failed-manifest path.
  }
  const replay = stringFlag(parsed, '--replay');
  if (!dryRun || replay !== undefined || adapter !== undefined) {
    if (config.models.length < config.minModels) {
      // Pipeline writes the auditable failed manifest even when roster is below minimum.
      if (config.models.length === 0) { io.err('Error: a generation run requires a model roster'); return 1; }
    }
    let transport = adapter;
    try { if (replay !== undefined) transport = readReplay(replay); }
    catch (error) { io.err(`Error: ${(error as Error).message}`); return 1; }
    if (dryRun && !transport) { io.err('Error: a dry run must use replay, never HTTP'); return 1; }
    return runPipeline({ config, request, policy, dryRun, adapter: transport }).then((result) => {
      io.out(`council propose${dryRun ? ' (dry run replay)' : ''}: status=${result.manifest.status} candidates=${result.candidates.length} dropped=${result.dropped.length}`);
      if (result.dir) io.out(`bundle=${displayPath(result.dir)}`);
      if (dryRun) {
        for (const candidate of result.candidates) io.out(`candidate: key=${candidate.key} lemma=${candidate.lemma} pos=${candidate.pos}`);
        for (const drop of result.dropped) io.out(`dropped: reason=${drop.reason}${drop.lemma ? ` lemma=${drop.lemma} pos=${drop.pos}` : ` key=${drop.key}`}`);
      }
      if (result.manifest.status === 'failed') io.err(`Error: ${result.manifest.error}`);
      return result.manifest.status === 'ok' ? 0 : 1;
    }).catch(() => { io.err('Error: council generation failed (configuration, prompt or output operation)'); return 1; });
  }
  io.out('council propose (dry run)');
  io.out('mode=propose');
  io.out(
    `request: topic=${topic ?? '(unset)'} cefr=${cefr ?? '(unset)'} count=${count === null ? '(unset)' : count}`
  );
  if (config.models.length === 0) {
    io.out('models=0 WARN no model roster configured (COUNCIL_MODELS or --models); a real run needs one');
  } else {
    io.out(`models=${config.models.length} [${config.models.join(', ')}]`);
  }
  io.out(`min_models=${config.minModels} timeout_s=${config.timeoutSeconds} temperature=${config.temperature}`);
  io.out(`prompt_version=propose:${config.promptVersion.propose} critique:${config.promptVersion.critique}`);
  io.out(`style_guide=${displayPath(config.paths.styleGuide)} sha256=${config.styleGuideSha256}`);
  io.out(`excluded_pairs=${exclusion.length} (skipped corpus files=${lexiconSkipped})`);
  const privacySource = policy.localSource === null ? '' : ` source=${displayPath(policy.localSource)}`;
  io.out(`privacy=ok local_terms=${policy.localTerms.length} hashed_terms=${policy.hashedTerms.length}${privacySource}`);
  for (const prompt of prompts) {
    io.out(`--- ${prompt.field} ---`);
    io.out(prompt.text.replace(/\n$/, ''));
  }
  return 0;
}

export function runCli(
  argv: string[],
  io: CliIo = CONSOLE_IO,
  env: Record<string, string | undefined> = process.env as Record<string, string | undefined>,
  adapter?: ProviderAdapter
): number | Promise<number> {
  const [verb, ...rest] = argv;
  switch (verb) {
    case 'validate':
      return runValidate(rest, io);
    case 'propose':
      return runPropose(rest, io, env, adapter);
    case 'privacy': {
      try {
        const [action, ...flags] = rest;
        if (action !== 'hash') throw new UsageError('usage: council privacy hash [--privacy <file>]');
        const parsed = parseArgs(flags, ['--privacy'], []);
        if (parsed.positionals.length) throw new UsageError('privacy hash takes no positional arguments');
        io.out(JSON.stringify(privacyHashDocument(stringFlag(parsed, '--privacy') ?? path.join(TOOL_ROOT, 'privacy.local.json')), null, 2));
        return 0;
      } catch (error) { io.err(`Error: ${(error as Error).message}`); return 1; }
    }
    case 'help':
    case '--help':
    case '-h': {
      io.out(USAGE);
      return 0;
    }
    case undefined: {
      io.err('Error: no command given');
      io.err(USAGE);
      return 1;
    }
    default: {
      io.err(`Error: unknown command "${verb}"`);
      io.err(USAGE);
      return 1;
    }
  }
}

const entry = process.argv[1];
if (entry !== undefined && path.resolve(entry) === fileURLToPath(import.meta.url)) {
  process.exitCode = await runCli(process.argv.slice(2));
}
