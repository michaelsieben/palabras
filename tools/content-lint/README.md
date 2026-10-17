# tools/content-lint

Validates every `content/words/*.json` entry against `content/schema/word.schema.json`
and against the Peninsular-Spanish denylist in `content/style-guide.lexicon.json`.

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

## CI wiring (NOT wired here)

`.github/workflows/ci.yml` belongs to another agent. The exact change to add — inside the
existing `build` job, after the Frontend `npm run build` step and before
"Build and smoke-test container" (Node 24 is already installed by that point):

```yaml
      - name: Lint content
        run: node tools/content-lint/index.js
```

No `setup-node` change and no `npm install` are needed: the script has no dependencies.
