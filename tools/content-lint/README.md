# tools/content-lint

Validates every `content/words/*.json` entry against `content/schema/word.schema.json`
and against the Peninsular-Spanish denylist in `content/style-guide.lexicon.json`.
Also lints every committed council bundle under `content/council/` (see below).

- **Zero dependencies.** Plain Node (`fs`, `path`); no install step, no network, no LLM.
  The JSON-Schema validator in `validate.js` implements the draft 2020-12 subset that the
  word schema actually uses (`$ref`, `type`, `enum`, `pattern`, `if/then`, …).
- **Exit code 1 on any error**, so it can gate CI. Warnings (the `tú`/`vosotros` check)
  do not fail the run.

## Run

```bash
node tools/content-lint/index.js
# or
npm --prefix tools/content-lint run lint
```

## What it checks

| Check | Severity |
| --- | --- |
| Schema validation of every entry | error |
| filename == `<id>.json` | error |
| duplicate `id` | error |
| duplicate sense (headword + gloss) | error |
| `register: "vulgar"` without `registerNotes` | error |
| LatAm denylist term as headword (`matchIn` includes `headword`) | error |
| LatAm denylist term inside an example (`matchIn` includes `example`) | error |
| example > 12 words | error |
| example with no terminal `.` `?` `!` | error |
| `?`/`!` without `¿`/`¡` | error |
| voseo | error |
| `usted`/`ustedes` in a non-`formal` example | warning |

The allowlist (`peninsularAllowlist`) documents accepted Peninsular forms and suppresses a
headword-only denylist hit for an explicitly allowlisted headword.

Per-entry escape hatch: `"lint": { "exemptTerms": ["term"] }` skips that denylist term for
that entry only; `notes` must explain why.

## Council bundles

`index.js` also lints every bundle under `content/council/` with `candidates.js`: manifest and
candidate schema, `candidates_sha256`, counts, unique keys, band/agreement, model ids,
`proposed_by`, sense alternative counts, and the §4.3 size guard (2 MB per `candidates.jsonl`,
10 MB for the directory). Contract problems fail the run; denylist hits in candidate text are
warnings (they become reviewer chips, never an import refusal).

```bash
node tools/content-lint/candidates.js              # lint content/council
node tools/content-lint/candidates.js <dir>        # lint one bundle or a parent of bundles
node tools/content-lint/candidates.js --self-test  # assert every tools/council fixture verdict
```

The self-test is the CI proof that this linter rejects a deliberately broken bundle, over the same
shared fixture set the C# validator and the council CLI use.

## CI wiring

Wired in `.github/workflows/ci.yml` inside the `build` job: `node tools/content-lint/index.js`
after the Frontend build, and `node tools/content-lint/candidates.js --self-test` next to it.
No `setup-node` change and no `npm install` are needed: the script has no dependencies.
