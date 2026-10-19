# tools/council — offline council CLI

The producer side of the council workflow (design ΔE/ΔF/ΔH). It is a **local, offline** tool: it
never runs in the image, never ships to the server, and nothing under `tools/` is copied into the
container (`/app/tools` is asserted absent by the E.0 check).

Implemented producer pieces:

| Piece | State |
| --- | --- |
| `src/validate.ts` | the CLI half of the shared bundle contract (T-4), over `tools/council/fixtures/` |
| `src/bundle.ts` | deterministic bundle writer: canonical JSONL, `candidates_sha256`, derived manifest fields |
| `src/privacy.ts` | ΔH guard: local plain-text terms + committed SHA-256-only terms, accent-folded scan |
| `src/config.ts` | env/`.env.council` layering, roster, prompt-version discovery, schema-owned vocabularies |
| `src/lexicon.ts` | corpus + committed bundles + additive override: exclusion list and occupied keys |
| `src/cli.ts` | `validate`, `propose`, `privacy hash`, `help` |
| pipeline modules | provider adapters, propose/critique/merge/score/alternatives/lint |

`propose` fans out proposals, merges and assigns stable keys, critiques in batches of 25, scores,
tops up alternatives once when needed, lints, writes and validates a bundle. `rework` and `report`
remain deferred to L2. Real runs and their data remain owner-gated; offline tests are not real-run acceptance.
`--dry-run` prints rendered prompts without network calls or writes. With `--replay <file>` it
previews the complete pipeline's candidates and dropped reasons instead.

## Install, build, test

```
cd tools/council
npm ci
npm run build     # tsc -> dist/
npm test          # node --test test/*.test.mjs (pretest rebuilds)
```

Zero runtime dependencies: the provider adapters use plain `fetch`, and the JSON-Schema subset
is the one the repo already ships for `content/words` (`tools/content-lint/validate.js`). Dev
dependencies are only TypeScript and its Node types.

## Commands

```
node dist/cli.js validate content/council [--lexicon <dir>] [--json]
node dist/cli.js validate tools/council/fixtures/valid-propose
node dist/cli.js propose --dry-run --topic food --cefr A2 --count 40
node dist/cli.js privacy hash --privacy privacy.local.json
node test/e2e-probe.mjs
node dist/cli.js help
```

- `validate <dir>...` accepts a bundle (a directory with `manifest.json`) or a parent of bundles.
  Exit 0 only when every bundle is valid; every defect is reported with its location, so a
  producer fixes one run instead of one error per run.
- `propose --dry-run` prints the prompts for the discovered versions, the roster, the style-guide
  hash, the exclusion-pair count and the privacy result. `--topic`, `--cefr`, `--count`,
  `--models`, `--replay`, `--style-guide`, `--privacy`, `--privacy-hashes`, `--lexicon`, `--prompts`,
  `--out`, `--env-file` and `--prompt-version` override the corresponding configuration.
- Exit codes are 0/1 only, matching `docs/06-content-cli.md`.
- `--hold` belongs to the backend's `import-council`, not the producer, and is refused here.
- Replay JSON maps model ids to reply queues: `{ "provider:model": [{"text":"...JSON..."},
  {"text":"...critique JSON..."}] }`. Errors use `{ "error": "auth" }` (also `http`, `timeout`,
  `invalid`, optional numeric `status`). Replay never calls HTTP. Non-dry replay writes a bundle
  and requires the same local privacy policy as a real run.

## Configuration

`env.example` is the template; copy it to `.env.council` (gitignored) and fill in the keys. Real
environment variables win over the file, and `--models` wins over `COUNCIL_MODELS`. Credentials are
read only by the provider adapters and are never printed: the dry run lists model ids, never
keys.

The request vocabularies (`topic`, `cefr`) are read from
`content/schema/council-manifest.schema.json`, and the prompt version is the highest
`prompts/<phase>.vN.md` on disk, so a new prompt is a new file, not an edit.

HTTP adapters support OpenRouter, OpenAI, Anthropic, Google and Mistral. Retry 429/5xx/timeouts
twice with exponential backoff; retry schema-invalid JSON once with a fixed repair instruction.
Raw response bodies, headers and keys are never logged or persisted. Failure errors contain
only phase, class and HTTP status. Both phases require `COUNCIL_MIN_MODELS` healthy models.

## Stable keys and dedupe

`es-<article-free, accent-folded lemma slug>-NN`, lowest free `01`–`99`, allocated in
`(slug, pos, lemma_norm)` order before critique. Keys never change after assignment, including
when lint swaps a lemma. Existing `(lemma_norm, pos)` pairs from corpus and committed bundles
are dropped as `exists` before allocation. `--lexicon` is additive, not a way to hide the corpus.
DB-only words are invisible to this offline lexicon; importer skip-by-key still prevents overwrite,
but a duplicate lemma can reach review. An export-lexicon command remains deferred.

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
- `privacy.sha256.json` - committed and **hash-only**. Unsalted SHA-256 names can be reversed
  with a dictionary: the committed list is a CI tripwire, not secrecy; the local list is the real
  guard. Keep the committed list empty unless the owner accepts that its entries are effectively
  public. Every non-dry run requires a present, non-empty `privacy.local.json`.
  Each entry is `{tokens, sha256}`. The version and normalisation must match the loader.
  Generate the entire sorted, deduplicated document with:

  ```
  node dist/cli.js privacy hash --privacy privacy.local.json
  ```

A hit aborts before any provider call and the message names the dirty field and the list index,
never the term. A term shorter than three characters after normalisation is refused: it would
match everything and protect nothing.
Hash format is v2: coarse tokens trim boundary punctuation and preserve addresses/domains;
fine tokens split all non-letter/non-number separators. The scanner checks both sequences.
Generator uses coarse tokens for terms containing `@` or `.`, fine tokens otherwise. v1 files
are refused; regenerate from the local list, never migrate old hashes in place. Duplicate entries
with identical widths are harmless; conflicting widths for one hash are refused.

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
