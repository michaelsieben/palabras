# tools/council — offline council CLI

The producer side of the council workflow (design ΔE/ΔF/ΔH). It is a **local, offline** tool: it
never runs in the image, never ships to the server, and nothing under `tools/` is copied into the
container (`/app/tools` is asserted absent by the E.0 check).

M3 C6 ships the skeleton:

| Piece | State |
| --- | --- |
| `src/validate.ts` | the CLI half of the shared bundle contract (T-4), over `tools/council/fixtures/` |
| `src/bundle.ts` | deterministic bundle writer: canonical JSONL, `candidates_sha256`, derived manifest fields |
| `src/privacy.ts` | ΔH guard: local plain-text terms + committed SHA-256-only terms, accent-folded scan |
| `src/config.ts` | env/`.env.council` layering, roster, prompt-version discovery, schema-owned vocabularies |
| `src/lexicon.ts` | the committed corpus as the exclusion list and known-key set |
| `src/cli.ts` | `validate`, `propose --dry-run`, `help` |
| C7 (not here) | provider adapters, propose/critique/merge/score/lint, `rework` |

A bare `council propose` refuses with exit 1 and says the pipeline lands in C7. `--dry-run` is the
useful half today: it prints the exact prompts, the resolved configuration and the exclusion count,
and runs the privacy guard - with no network call and no file written.

## Install, build, test

```
cd tools/council
npm ci
npm run build     # tsc -> dist/
npm test          # node --test test/*.test.mjs (pretest rebuilds)
```

Zero runtime dependencies: the provider adapters (C7) use plain `fetch`, and the JSON-Schema subset
is the one the repo already ships for `content/words` (`tools/content-lint/validate.js`). Dev
dependencies are only TypeScript and its Node types.

## Commands

```
node dist/cli.js validate content/council [--lexicon <dir>] [--json]
node dist/cli.js validate tools/council/fixtures/valid-propose
node dist/cli.js propose --dry-run --topic food --cefr A2 --count 40
node dist/cli.js help
```

- `validate <dir>...` accepts a bundle (a directory with `manifest.json`) or a parent of bundles.
  Exit 0 only when every bundle is valid; every defect is reported with its location, so a
  producer fixes one run instead of one error per run.
- `propose --dry-run` prints the prompts for the discovered versions, the roster, the style-guide
  hash, the exclusion-pair count and the privacy result. `--topic`, `--cefr`, `--count`,
  `--models`, `--hold`, `--style-guide`, `--privacy`, `--privacy-hashes`, `--lexicon`, `--prompts`,
  `--out`, `--env-file` and `--prompt-version` override the corresponding configuration.
- Exit codes are 0/1 only, matching `docs/06-content-cli.md`.

## Configuration

`env.example` is the template; copy it to `.env.council` (gitignored) and fill in the keys. Real
environment variables win over the file, and `--models` wins over `COUNCIL_MODELS`. Credentials are
read only by the provider adapters (C7) and are never printed: the dry run lists model ids, never
keys.

The request vocabularies (`topic`, `cefr`) are read from
`content/schema/council-manifest.schema.json`, and the prompt version is the highest
`prompts/<phase>.vN.md` on disk, so a new prompt is a new file, not an edit.

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
`{{NAME}}` and are filled by the C7 normaliser/renderer:

| Prompt | Placeholders |
| --- | --- |
| `propose.vN.md` | `{{REQUEST}}`, `{{STYLE_GUIDE}}`, `{{CANDIDATE_SCHEMA}}`, `{{EXCLUSION_LIST}}` |
| `critique.vN.md` | `{{STYLE_GUIDE}}`, `{{CANDIDATE_SCHEMA}}`, `{{CANDIDATES}}` |

Neither prompt names the person who reviews the queue, and neither carries learner data (ΔH).

## Privacy guard

Two lists, normalised identically (NFC, lower-case, accent-folded, whitespace collapsed):

- `privacy.local.json` - gitignored; template in `privacy.local.example.json`. Real names,
  usernames, email, the domain. Matched as normalised substrings, so an address stays one hit.
- `privacy.sha256.json` - committed and **hash-only**, so CI and the lint can run the same scan
  without the strings entering git. Each entry is `{tokens, sha256}`; the scan hashes every n-gram
  of that width. Generate one entry with:

  ```
  node -e "import('./dist/privacy.js').then(m=>{const n=m.normaliseTerm('the-term');console.log(JSON.stringify({tokens:m.tokenise(n).length,sha256:m.termHash(n)}))})"
  ```

A hit aborts before any provider call and the message names the dirty field and the list index,
never the term. A term shorter than three characters after normalisation is refused: it would
match everything and protect nothing.

## Parity with the C# importer and the CI linter

All three read `content/schema/council-*.schema.json` and the fixtures in
`tools/council/fixtures/cases.json`. `test/validate.test.mjs` asserts the same accept/reject
verdict per fixture as `M3CouncilBundleTests` (C#) and `node tools/content-lint/candidates.js
--self-test`.

Known, deliberate gap: this validator does not reject a JSON document with duplicate object keys
(the C# reader does, because `System.Text.Json` would silently keep the last one). A producer
that emits duplicate keys is a bug no fixture covers; do not rely on the CLI to catch it.
