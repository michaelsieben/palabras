# tools/council — offline council CLI

The producer side of the council workflow (design ΔE/ΔF/ΔH). It is a **local, offline** tool: it
never runs in the image, never ships to the server, and nothing under `tools/` is copied into the
container (`/app/tools` is asserted absent by the E.0 check). The consumer side is the backend's
`import-council` verb and startup sync (`docs/06-content-cli.md`).

Status: implemented in M3 C6 (`10050d6`) and C7 (`a3cdfff`); 70/70 council tests pass. **No real
bundle exists yet.** The first one is the owner's real run (M3 C9), which needs provider keys and a
local privacy list. Offline tests and replay runs are not real-run acceptance.

| Piece | Role |
| --- | --- |
| `src/cli.ts` | `validate`, `propose`, `privacy hash`, `help` |
| `src/config.ts` | env/`.env.council` layering, roster, prompt-version discovery, schema-owned vocabularies |
| `src/privacy.ts` | ΔH guard: local plain-text terms + committed SHA-256-only terms, accent-folded scan |
| `src/lexicon.ts` | corpus + committed bundles + additive override: exclusion list and occupied keys |
| `src/pipeline.ts` | propose → cluster/merge → keys → critique → score → alternatives/top-up → lint → write → validate |
| `src/provider.ts`, `src/providers/` | privacy chokepoint, retries, HTTP adapter, replay adapter |
| `src/bundle.ts` | deterministic bundle writer: canonical JSONL, `candidates_sha256`, derived manifest fields |
| `src/validate.ts` | the CLI half of the shared bundle contract (T-4), over `tools/council/fixtures/` |

Merge, score and lint are pipeline stages inside `propose`, not separate commands. `rework`,
`report` and an export-lexicon command are deferred (L2).

## Prerequisites

- Node (CI uses 24.21.0) and `npm ci` in `tools/council`.
- A roster and keys in `tools/council/.env.council` (see Configuration). Not needed for `validate`,
  `privacy hash` or a dry run.
- A non-empty `tools/council/privacy.local.json` for every non-dry run (see Privacy guard). A real
  run without it exits 1 before any provider call.

## Install, build, test

```
cd tools/council
npm ci
npm run build     # tsc -> dist/
npm test          # node --test test/*.test.mjs (pretest rebuilds)
```

Zero runtime dependencies: the provider adapters use plain `fetch`, and the JSON-Schema subset
is the one the repo already ships for `content/words` (`tools/content-lint/validate.js`). Dev
dependencies are only TypeScript and its Node types. CI runs `npm ci` + `npm test` here in the
`Council CLI (build and unit tests)` step.

## Commands

Run from `tools/council` after `npm run build`:

```
node dist/cli.js propose --dry-run --topic food --cefr A2 --count 40
node dist/cli.js propose --topic food --cefr A2 --count 40
node dist/cli.js validate ../../content/council [--lexicon <dir>] [--json]
node dist/cli.js validate fixtures/valid-propose
node dist/cli.js privacy hash --privacy privacy.local.json
node dist/cli.js help
```

- `propose` runs the whole pipeline and writes one bundle. `--topic` and `--cefr` must be values from
  `content/schema/council-manifest.schema.json`; `--count` is 1–500. Further overrides:
  `--models`, `--style-guide`, `--privacy`, `--privacy-hashes`, `--lexicon`, `--prompts`, `--out`,
  `--env-file`, `--prompt-version`, `--replay`.
- `propose --dry-run` prints the rendered prompts, the roster (ids only), min models, timeout,
  temperature, prompt versions, the style-guide hash, the exclusion-pair count and the privacy
  result. No network call, no write. The local privacy list is optional here unless `--privacy` is
  given.
- `validate <dir>...` accepts a bundle (a directory with `manifest.json`) or a parent of bundles.
  Exit 0 only when every bundle is valid; every defect is reported with its location.
- `privacy hash` prints the committed hash-list document (see Privacy guard).
- Exit codes are 0/1 only, matching `docs/06-content-cli.md`. A `propose` run with status `failed`
  exits 1.
- `--hold` belongs to the backend's `import-council`, not the producer; `propose --hold` exits 1
  with `unknown option --hold`.

### `--replay <file>`: preview and test aid only

`--replay` swaps the provider for canned replies from a JSON file and never calls HTTP. It always
implies `--dry-run`, and no flag combination overrides that: it writes nothing, anywhere. It previews
the candidates and dropped reasons the pipeline would produce:

```
node dist/cli.js propose --replay replies.json --models anthropic:a,openai:b
```

File shape: model id → reply queue, consumed in call order (propose first, then each critique
batch, then any top-up):

```json
{
  "anthropic:a": [{ "text": "{\"candidates\":[...]}" }, { "text": "{...critique JSON...}" }],
  "openai:b": [{ "error": "timeout" }]
}
```

Errors are `{ "error": "auth" | "http" | "timeout" | "invalid", "status"?: <number> }`. An empty
queue counts as an invalid reply.

**A replay never writes a bundle.** Its candidates come from hand-written replies, not models, so
there is no importable output and nothing to commit. Only a run without `--replay` writes a bundle
(see What a run writes).

## Configuration

`env.example` is the template; copy it to `.env.council` (gitignored) and fill in the keys. Real
environment variables win over the file, `COUNCIL_ENV_FILE` or `--env-file` picks another file, and
`--models` wins over `COUNCIL_MODELS`. Only `COUNCIL_*` and `*_API_KEY` values are read.

| Variable | Default | Meaning |
| --- | --- | --- |
| `COUNCIL_MODELS` | none | comma-separated `provider:model` ids, at most 8, no duplicates |
| `COUNCIL_MIN_MODELS` | 2 | healthy models needed in each phase |
| `COUNCIL_TIMEOUT_S` | 300 | per-request timeout |
| `COUNCIL_TEMPERATURE` | 0.4 | sampling temperature, both phases |
| `*_API_KEY` | none | Anthropic, OpenAI, Google, Mistral, or one OpenRouter key |

The model ids in `env.example` are placeholders; the roster is chosen at the first real run
(`docs/02-open-questions.md` Q12). The request vocabularies (`topic`, `cefr`) are read from
`content/schema/council-manifest.schema.json`, and the prompt version is the highest
`prompts/<phase>.vN.md` on disk, so a new prompt is a new file, not an edit.

### Credentials and cost

- Keys are read only by the provider adapters and are never printed or persisted. The dry run
  lists model ids, never keys. Raw response bodies and headers are never logged or written; there
  is no `raw/` output.
- A real run sends paid API requests to every roster model: one propose call each, one critique
  call per batch of 25 candidates each, plus at most one top-up call per candidate that is short of
  gloss alternatives. Retries add calls (below).
- The manifest records `tokens.in`/`tokens.out` per model, as reported by the provider. It does not
  fill `cost_usd_est`; multiply tokens by the provider's price yourself.
- Provider-side retention and training settings are not checked by the tool (`design-amendment.md`
  ΔH); check them when the roster is chosen.

HTTP adapters support OpenRouter, OpenAI, Anthropic, Google and Mistral. 429, 5xx and timeouts are
retried twice with exponential backoff; schema-invalid JSON is retried once with a fixed repair
instruction. Failure errors contain only phase, class and HTTP status.

## What a run writes

`propose` without `--dry-run` writes `<out>/<run_id>/` (default `content/council/<run_id>/`, run id
is a random UUID):

| File | When | Content |
| --- | --- | --- |
| `manifest.json` | always | run id, mode `propose`, prompt versions, style-guide hash, request, per-model phase status/latency/tokens/error class, derived counts, `status` (`ok`/`failed`), `error` |
| `candidates.jsonl` | `status: "ok"` only | one candidate per line, canonical JSON, hashed into `candidates_sha256` |
| `dropped.jsonl` | `ok`, or a non-privacy failure | audit rows `{reason, lemma, pos}` or `{reason, key}`; never imported |

After writing, the pipeline validates the bundle with the same rules as `validate`. If that fails,
the bundle is rewritten as a failed manifest (`error: "bundle validation failed"`) without
candidates.

Drop reasons: `exists`, `unkeyable_lemma`, `key_space_exhausted`, `low_agreement`,
`insufficient_alternatives`.

### Failure causes

The causes below end the run with exit 1 and no `candidates.jsonl`. A cause detected before the
pipeline starts (the privacy pre-scan, initial prompt rendering) writes nothing at all and reports
on stderr; a cause detected inside it writes a failed manifest whose `error` names only the field or
class, never the offending text:

- **Privacy block.** A private term in the style guide, a template, the exclusion list or the initial
  rendered prompts aborts before any provider call with `PrivacyBlockedError` on stderr, writing
  nothing. A term in a retry, a critique batch, a top-up, a model reply or the final output fails
  inside the pipeline and writes a failed manifest with no candidates and no `dropped.jsonl`.
- **Too few healthy models.** Fewer than `COUNCIL_MIN_MODELS` models succeeded in propose or in
  critique (`healthy models below COUNCIL_MIN_MODELS`). A run with an empty roster exits 1 without
  a manifest.
- **Leftover placeholder.** The renderer refuses any `{{` left after rendering. This includes model
  output re-sent in `{{CANDIDATES}}` to critique: a model reply containing a literal `{{` fails the
  run. This is deliberate.
- **Nothing left.** `no importable candidates remain` after keys, scoring and alternatives.
- **Validation.** The written bundle fails the contract (`bundle validation failed`).

### Getting a bundle into the repo

1. Run `propose` (real run, keys and local privacy list present). Output lands in
   `content/council/<run_id>/`.
2. Check it: `node dist/cli.js validate ../../content/council`, and from the repo root
   `node tools/content-lint/candidates.js` (the CI backstop).
3. Spot-check the multi-sense candidates (see Merge caveat).
4. Commit `manifest.json`, `candidates.jsonl` and `dropped.jsonl`. Committing a failed manifest is
   allowed as an audit record; the importer refuses it.
5. The image copies `content/council/` and the startup sync imports `propose` bundles as
   `pending_review` (`docs/06-content-cli.md`, `import-council`).

C9 also adds a CI `validate content/council` step together with the first committed bundle; it does
not exist yet.

## Stable keys and dedupe

Key = `es-<slug>-NN`:

- `slug` is the lemma without a leading `el`/`la`/`los`/`las`, lower-cased, accents folded (NFD,
  combining marks stripped), every run of non-`[a-z0-9]` replaced by `-`, edges trimmed. `año` and
  `ano` share the slug `ano`.
- `NN` is the lowest free ordinal `01`–`99`. `pos` is never part of the key: homographs such as
  `zorzal` noun/adjective get `es-zorzal-01` and `es-zorzal-02`.
- Keys are allocated in `(slug, pos, lemma_norm)` order before critique, and never change after
  assignment, including when lint swaps a lemma. Never re-derive or rename a committed key.
- Occupied keys = the real corpus `content/words` (always loaded), every committed
  `content/council/*/candidates.jsonl` (a corrupt line throws instead of freeing a key), keys
  assigned earlier in the same run, and anything under `--lexicon` (additive only; it cannot hide
  the corpus).
- A candidate whose `(lemma_norm, pos)` already exists in that lexicon is dropped as `exists`
  before allocation. A slug that is empty or invalid drops as `unkeyable_lemma`; all 99 ordinals
  taken drops as `key_space_exhausted`.

Residual risk: words that exist only in the database (not in `content/words` or a committed bundle)
are invisible to this offline lexicon. The importer's skip-by-key still prevents an overwrite, but
a duplicate lemma under a new key can reach the review queue. An export-lexicon command is deferred.

## Merge caveat

Proposals for the same `(lemma, pos)` are merged by plurality with a stable roster tie-break.
Senses are aligned **by position**: sense 0 of every model is voted together, and so on. No proposed
sense is lost, but two models that list the same senses in a different order get cross-voted. The
result is a wrong primary gloss or a spurious `Distinct proposal gloss` alternative. Critique
(`duplicate_meaning`, `wrong_gloss`) and the reviewer chips are the backstop.

For the first real bundle (C9), spot-check every multi-sense candidate. If misalignment shows up,
open a follow-up ticket for gloss-similarity alignment.

## Bundle writer

`writeBundle({ outDir, runId, mode, promptVersion, styleGuideSha256, models, candidates })` writes
`<outDir>/<runId>/{manifest.json,candidates.jsonl,dropped.jsonl}`. It:

- serialises each candidate with the backend's canonical JSON rule (object keys sorted ordinally,
  arrays in order), so two runs over the same data produce byte-identical files and hashes;
- terminates `candidates.jsonl` with a newline and stores `candidates_sha256` over those exact
  bytes - the hash the importer verifies before any write;
- derives `models_requested`, `models_succeeded`, `reduced_confidence` and `counts` instead of
  trusting a producer, so a hand-written manifest cannot contradict itself;
- stamps `council.run_id` on every candidate, so a bundle cannot contain a candidate from another
  run;
- refuses an existing run id unless asked to overwrite, and writes **no** `candidates.jsonl` for a
  `status: "failed"` run (design ΔE: a failed run is an audit record, not importable input).

## Prompts

`prompts/propose.v1.md` and `prompts/critique.v1.md` are versioned templates. Placeholders are
`{{NAME}}` and are filled by a single-pass renderer:

| Prompt | Placeholders |
| --- | --- |
| `propose.vN.md` | `{{REQUEST}}`, `{{STYLE_GUIDE}}`, `{{CANDIDATE_SCHEMA}}`, `{{EXCLUSION_LIST}}` |
| `critique.vN.md` | `{{REQUEST}}`, `{{STYLE_GUIDE}}`, `{{CANDIDATE_SCHEMA}}`, `{{CANDIDATES}}` |

Neither prompt names the person who reviews the queue, and neither carries learner data (ΔH).
Unknown or leftover placeholders fail; inserted values are not re-expanded. `prompts/SHA256SUMS`
pins every versioned file in tests. Top-up reuses the critique prompt with one candidate.

## Privacy guard

Two lists, normalised identically (NFC, lower-case, accent-folded, whitespace collapsed):

- `privacy.local.json` - gitignored; template in `privacy.local.example.json`. Real names,
  usernames, email, the domain. Matched as normalised substrings, so an address stays one hit.
  **Every non-dry run requires a genuine, complete, non-empty copy**; this list is the real guard.
  A real run without it exits 1 before any provider call. The CLI checks only that the list is
  non-empty, never that it is complete: nothing else protects a name you did not list, so keeping
  it complete is the owner's responsibility.
- `privacy.sha256.json` - committed, **hash-only and optional**. It is empty by default and that is
  a supported state. Unsalted SHA-256 hashes of names can be reversed with a name list, so anything
  committed there is effectively public; publishing it is an owner decision, and an empty list
  implies no consent to publish personal data. This list is CI defence-in-depth only, separate from
  the runtime guard, and never required for a real run.

The committed file uses hash format v2 and lists `{tokens, sha256}` entries; its version,
normalisation and algorithm must match the loader. A v1 file is refused. Regenerate it from the
local list instead of migrating old hashes. Export refuses any term whose token count falls outside
1-8, and then writes no document at all:

```
node dist/cli.js privacy hash --privacy privacy.local.json > privacy.sha256.json
```

If the owner does choose to publish hashes, never redirect straight at the committed file: the shell
truncates it before the export can fail. Export to a private temporary path outside the repository,
confirm exit 0 and a valid non-empty document, compare it privately against the intended local
terms, then replace `privacy.sha256.json`. The output is sorted and deduplicated and contains no
clear text.

The CI step `Council committed privacy hash guard` (`npm run privacy:check`) scans `content/` and
`prompts/` against the committed list and never reads the local list:

| Result | Condition |
| --- | --- |
| pass, exit 0 | valid v2 document, including `terms: []`; the empty case prints an explicit NOTICE |
| pass, exit 0 | non-empty list and no hit in any scanned `.json`, `.jsonl` or `.md` file |
| fail, exit 1 | missing file, or JSON that cannot be parsed |
| fail, exit 1 | missing or unknown `schema`, `algorithm` or `normalisation` version |
| fail, exit 1 | any plaintext term, or an entry with a property other than `tokens`/`sha256` |
| fail, exit 1 | one hash given two different token counts |
| fail, exit 1 | malformed hash: `tokens` not an integer 1-8, or `sha256` not 64 lower-case hex |

An empty committed list therefore never turns CI red; it only means the hash backstop protects
nothing until the owner populates it.

A hit aborts before any provider call and the message names the dirty field and the list index,
never the term. A term shorter than three characters after normalisation is refused: it would
match everything and protect nothing.
Hash format v2: coarse tokens trim boundary punctuation and preserve addresses/domains;
fine tokens split all non-letter/non-number separators. The scanner checks both sequences.
The generator uses coarse tokens for terms containing `@` or `.`, fine tokens otherwise. Duplicate
entries with identical widths are harmless; conflicting widths for one hash are refused.

Every full rendered prompt is scanned immediately before every adapter call (including retries,
critique and top-up). Model replies and final bundle text are scanned too. Privacy failures write
only a field/class-only failed manifest, never candidates or dropped text.

## Parity with the C# importer and the CI linter

All three read `content/schema/council-*.schema.json` and the fixtures in
`tools/council/fixtures/cases.json`. `test/validate.test.mjs` asserts the same accept/reject
verdict per fixture as `M3CouncilBundleTests` (C#) and `node tools/content-lint/candidates.js
--self-test`.

Known, deliberate gap: this validator does not reject a JSON document with duplicate object keys
(the C# reader does, because `System.Text.Json` would silently keep the last one). A producer
that emits duplicate keys is a bug no fixture covers; do not rely on the CLI to catch it.
