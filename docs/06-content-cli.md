# Operator CLI: content, cards and progress restore

Shipped in T-20261018-0124 (M2 content importer and owner approval, M5 card lifecycle, M10 restore). Account
commands are in `04-accounts.md`; the learner HTTP API is in `07-learn-api.md`; the backup/restore procedure is in
`restore-drill.md`.

> The parallel `Vocabulary` model and `GET /api/words` approach (remote commit `e0e7f40`, T-20261017-1516) was
> superseded by the `Content` schema documented here and removed during the T-20261018-2005 reconciliation.

## How the CLI runs

The same binary is the web app and the CLI. When the first argument is `user`, `import`, `import-council`, `content`,
`learn` or `restore-progress`, `Program.cs` runs that verb and exits; no web host starts. Configuration comes from
`appsettings.json` and environment variables only, so `DATA_DIR` (default `/data`, database `palabras.db`) and
`ConnectionStrings__Default=Data Source=...` select the database exactly as for the app (`04-accounts.md`).

```sh
# Unraid, inside the running container
docker exec -it palabras /app/Palabras content status
# Local checkout, against a throwaway database
ConnectionStrings__Default='Data Source=/tmp/palabras-dev.db' \
  dotnet run --project Backend/Palabras -- content status
```

Common behaviour:

- Exit code `0` on success, `1` on any error. Errors go to stderr as `Error: <message>`. A database failure prints
  only a generic `Database operation failed; ... transaction rolled back.` line, never the EF/SQLite detail.
- Every write verb runs in one SQLite transaction: it either commits fully or changes nothing (`import-council` opens
  one per bundle, so a bad bundle cannot roll back its siblings).
- Ctrl-C prints `Command cancelled.` and exits `1`.
- `user`, `import` (without `--dry-run`), `import-council` (without `--dry-run`) and all `content` verbs create the
  database and apply pending migrations first (`DatabaseLifecycle.InitializeAsync`), as the app does at startup.
  `import --dry-run`, `import-council --dry-run` and `restore-progress` never create or migrate a database.
- These verbs have no HTTP equivalent. Anyone who can run them has full content and progress control.

Migrations in order: `InitialAccounts` (M1), `ContentSchema` (M2, `20261017234822`), `SrsCards` (M5,
`20261018000241`), `DayStatusAndSkips`, `RolloverCutoverMarker` (M7), `ReviewGate` (M4, `20261019140735`),
`CouncilImport` (`20261019182611`), `WordDifficulty` (`20261019192721`), in
`Backend/Palabras/Persistence/Migrations/`.

## Verbs

| Verb | Touches | Idempotent | Output on success |
| --- | --- | --- | --- |
| `user …` | `users` | per sub-command, see `04-accounts.md` | per sub-command |
| `import <dir> [--dry-run]` | `words`, `senses`, `word_alternatives`, `word_topics`, `word_revisions`, card epochs/suspension | yes: re-run with the same files → all `unchanged` | `created=N updated=N unchanged=N rejected=N` |
| `import-council <dir> [--dry-run] [--force] [--hold]` | `council_runs`, `council_model_results`, `word_provenance`, and the same content tables as `import` | yes: a recorded `run_id` is a strict no-op; a rework whose content hash is unchanged writes nothing | per-bundle lines, then `bundles=N imported=N created=N skipped=N failed=N` |
| `content approve <key…>` | `words.status`, `word_revisions`, `cards` | yes: already approved → `unchanged` | `approved=N unchanged=N` |
| `content reject <key…>` | `words.status`, `word_revisions`, `cards` | yes: already rejected → `unchanged` | `rejected=N unchanged=N` |
| `content approve --all-pending` | same | yes | `approved=N unchanged=N` |
| `content approve --top N [--dry-run]` | same | row-level only: a re-run approves the **next** N pending words | `approved=N unchanged=N`; dry run: one key per line, then `dry-run=N` |
| `learn settings --username <u> [--new-per-day N] [--max-reviews-per-day N] [--backlog-threshold N]` | `user_settings` (upsert) | yes | `user=…`, then one `<knob>=<old> -> <new>` line per knob (no `->` without flags) |
| `learn seed-known --username <u> --top N [--due-days N] [--dry-run] [--reset]` | that user's `cards` of the top N approved words | yes | `seeded_words=N seeded_cards=M` / `reset_words=N reset_cards=M`; dry run: keys, then `dry-run=N` |
| `content status` | no content rows; still applies pending migrations first, like every `content` verb, so stop it with the app during an upgrade (`08-upgrade-rollback.md`) | yes | one `<status>=<count>` line per status, then `total=N` |
| `content retire <key…>` | `words.status`, `word_revisions`, `cards` | yes: already retired is skipped | `retired=N` |
| `restore-progress <file.json>` | `cards`, `review_log` of one user | yes: identical rows are skipped | `Restored N cards and M reviews for user U.` |

Unknown or malformed arguments print the verb's usage line and exit `1` before the database is opened. Keys never
start with `-`; duplicates in a key list are collapsed.

### `import <dir> [--dry-run]`

Loads every `*.json` file directly in `<dir>` (not recursive) into the content tables. The argument is a path as
the process sees it; inside the container that is a container path. The image ships `content/schema/` only (the word
and council schemas), not `content/words/` (`Dockerfile:18`); copy them in with `docker cp`
(`10-bring-up.md` §4).

1. **Validate the whole batch before any write.** Each file must parse, contain no duplicate JSON property, pass
   `content/schema/word.schema.json` (JSON Schema, format validation on), have a file name equal to `<id>.json`, and
   carry a `createdAt` that parses as a timestamp. If any file fails, every failure is printed to stderr as
   `<file>: <reason>`, nothing is written, and the exit code is `1`. An empty directory is a successful no-op.
2. **Upsert by key.** The key is the file `id` (for example `es-coger-01`). `content_hash` is the lowercase hex
   SHA-256 of a canonical JSON form: object keys sorted ordinally, string escapes decoded, and the top-level
   `status`, `review` and `updatedAt` excluded. So a status-only or timestamp-only edit is `unchanged`.
   - Same hash → `unchanged`. Nothing is touched; an approved word stays approved, and a reviewer edit survives
     (section below).
   - New key → `created`, `version` 1, status `approved` with `approved_version` 1, one `word_revisions` row with
     `action='import'` whose snapshot says `approved`, and the learner cards are created in the same transaction
     (trust-by-default, `7234a15`). No `content approve` is needed; a reviewer objects afterwards
     (`09-review-api.md`, "Review policy").
   - Different hash → `updated`: `version` + 1 and a new `import` revision. An `approved` word without a reviewer
     `edit` revision stays `approved` (`approved_version` follows, card `epoch` + 1, no suspension). Every other word —
     approved with a reviewer edit, or `pending_review`, `rejected`, `needs_change`, `draft` — goes to
     `pending_review` and every card of the word is invalidated (`epoch` + 1, `suspended_reason='word_not_approved'`
     unless a reason is already set). FSRS state is kept.
3. **File `status` is advisory.** On creation only `draft` is honoured (the word lands `draft`, no cards); every other
   value, including `pending_review`, `needs_change` and `rejected`, lands as `approved`. On update the file status is
   ignored; the rule above decides.
4. **Senses keep their ids by position.** A sense that disappears from the file is deleted only if it has no cards;
   otherwise the row stays (review history references it) and its cards get `suspended_reason='sense_removed'`
   unless they are already `manual` or `leech`. Alternatives are replaced; topics are synchronised.
5. **A missing file never retires a word.** A partial directory must not retire the corpus. Use `content retire`.

#### Re-import after a reviewer edit (Q-M4-3)

Shipped in `89657e4` (run `T-20261019-2013` step 4). `content_hash` records the last **imported source file**, not the
current database content. `approve_edit` and `approve_alt` never rewrite it, so the next import can still tell whether
the file changed:

- **Source unchanged** → `unchanged`, and the importer writes nothing at all: the reviewer's edit, the approved status,
  `version`, `content_hash`, her verdict row, the alternatives and the card epochs stay exactly as they were.
  Re-running the import changes nothing else.
- **Source changed, word carries a reviewer edit** (an `edit` revision from `approve_edit`/`approve_alt`) →
  `updated`, and the source wins the content: current content is replaced, `version` + 1, status back to
  `pending_review`, cards demoted (`epoch` + 1). Nothing historical is deleted or rewritten — the reviewer's edit
  snapshot stays at its old version, her `word_reviews` verdict still names the version she saw, and `approved_version`
  stays the last approved version (an audit fact, not approval of the new version). The edit marker is permanent: once
  a word has an `edit` revision, every later changed-source import demotes it (`09-review-api.md`).
- **Source changed, approved word, no reviewer edit** → `updated`, content replaced, `version` + 1, stays `approved`.
- In both cases a reviewer action still queued against the old version returns `stale_version`; the queue shows the
  new version.
- **Repeating an import** is idempotent in both states: unchanged repeats stay `unchanged`, and a second run after a
  changed import changes nothing further.

The whole batch runs in one `BEGIN IMMEDIATE` transaction, the same lock a reviewer batch takes, so an import and a
reviewer action serialise instead of acting on a stale read. No migration was added for this behaviour, so the rollback
story in `08-upgrade-rollback.md` section D.2 is unchanged.

What "unchanged" means: the hash covers content only. Object key order, JSON escapes and whitespace do not change it,
and an edit to root-level `status`, `review` or `updatedAt` alone is still `unchanged`. Array order *is* part of the
hash, including `topics` (caveat below).

Reviewer edits are validated against the import schema's field constraints (lengths, enums, accepted-answer lists, the
noun/phrase gender rule, and the 60-character headword limit even for a longer alternative value), so a reviewer cannot
write a value the importer would reject. Lexical/style lint of edits is still deferred (Q-M4-4): no lexicon matcher
runs, so an edit is not checked for Peninsular usage, register or LATAM forms, and `lintWarnings` is not recomputed.
The consequence: a hand-edit can pass every server check and still be a style regression; the reviewer UI and her
judgement are the only guard there.

Known caveats of the import side (the reviewer-side ones — multi-edit rejection, the import-then-edit test gap,
`lintWarnings` — are in `09-review-api.md` Known limitations):

- Reordering the `topics` array changes the hash without changing meaning, so a topics-only re-export bumps the version
  (and, for a reviewer-edited or non-approved word, sends it back to `pending_review`). The safe direction — a reviewer
  edit is never lost — still holds.
- The importer loads senses, alternatives and topics one word at a time inside the write lock (N+1); fine at this
  corpus size, worth batching only if it grows.
- `content approve` (`Content/ContentApproval.cs:17`) still opens its transaction deferred, so a CLI approval racing an
  import can fail with SQLite `SQLITE_BUSY_SNAPSHOT` / "database is locked". Nothing is corrupted, nothing is partly
  written, and the command can be re-run; aligning it with the `BEGIN IMMEDIATE` pattern is later work.

Optional per-sense fields `acceptedEn`, `acceptedEs` and `accentsAreThePoint`, and the word field `plural`, are read
when present. Without them the accepted English answers are the gloss split on `", "` and the accepted Spanish answer
is the headword.

`--dry-run` reports the same four counts, prefixed `Dry run: `, through a read-only connection. With no database, or
a database without the `words` table, every valid file counts as `created`.

Only lower-case `.json` is matched; on Linux `X.JSON` is skipped silently.

### `import-council <dir> [--dry-run] [--force] [--hold]`

Loads council bundles into the provenance and content tables (M3b). A **bundle** is a directory containing
`manifest.json`; `<dir>` is either one bundle or a parent directory of bundles (`*/manifest.json`, ordinal order),
the same discovery rule the startup sync uses. Flags may appear in any order; a duplicate flag or a second positional
argument is a usage error before the database is opened. One stdout line per accepted bundle, then the summary
`bundles=N imported=N created=N skipped=N failed=N`, where `skipped` covers `unchanged`, `skipped_existing` and
`skipped_reviewer_edit_newer`. Exit `0` when no bundle was refused (imported, already imported, or all candidates
skipped/unchanged) and on a clean dry run; `1` on any refusal, usage/validation error, hash mismatch or database
failure. Refusals go to stderr as
`Error: <run-id|dir>: <reason>`; DB failures print only
`Error: Database operation failed; council import transaction rolled back.`

#### Bundle contract

Committed schemas: `content/schema/council-manifest.schema.json` (`palabras.council.manifest.v1`) and
`content/schema/council-candidate.schema.json`. Both are validated by the C# importer (JSON Schema, format checks on,
unknown properties refused at every level) against the shared fixtures under `tools/council/fixtures/`; the
`tools/council` validator (`council validate`, C6) consumes the same schemas and fixtures.

#### Producer side

Bundles are produced offline by the council CLI (`tools/council`, M3 C6/C7). Usage, privacy list, keys, costs and
failure causes are in `tools/council/README.md`; this section covers only what crosses into the repo.

- `council propose` writes `content/council/<run_id>/` directly (default `--out`): `manifest.json` always,
  `candidates.jsonl` only when `status` is `ok`, and `dropped.jsonl` as audit. A failed run leaves a manifest without
  candidates, which this importer refuses. No `raw/` directory is written.
- Keys are assigned by the producer as `es-<slug>-NN` (lemma without article, accents folded, lowest free `01`–`99`,
  never from `pos`) against the corpus and every committed bundle, and are never re-derived. Pairs already in that
  offline lexicon are dropped as `exists`; a word that exists only in the database can still arrive under a new key, and
  is then a duplicate for the reviewer to reject (skip-by-key prevents an overwrite, not a new key).
- The producer validates its own bundle before finishing. The owner then commits the bundle directory; the image copies
  `content/council/` and the startup sync imports it (below). `node tools/content-lint/index.js` lints committed bundles
  in CI.
- `--replay` output is a preview/test artefact and must never be committed.
- Policy is unchanged by the producer (`7234a15`): corpus `import` approves on arrival, council `propose` candidates
  still land as `pending_review` and need a reviewer approval before they reach learners.

No real bundle has been committed yet; the first is the owner's real run (M3 C9).

- `manifest.json` — required. Carries `run_id` (path-safe, 3–64 chars, also the bundle directory name), `mode`
  (`propose` or `rework`), `prompt_version`, `style_guide_sha256`, the request block, the model roster with per-phase
  status (`ok`/`failed`/`timeout`/`invalid`), `models_requested`, `models_succeeded`, `reduced_confidence`, `counts`,
  `status` and `created_at`. The roster still lists a failed model; `models_requested == models.length`,
  `models_succeeded.<phase>` must equal the models reporting that phase `ok`, and `reduced_confidence` must agree
  with those counts.
- `candidates.jsonl` — required when `status` is `ok`, one candidate per line. `candidates_sha256` must equal the
  SHA-256 of the raw file bytes (trailing newline included), `counts.candidates` must equal the line count (zero
  candidates is refused), and every candidate's `council.run_id` must equal `manifest.run_id`.
- `council.proposed_by` — **required and validated.** It lists the model ids that proposed the winning candidate
  itself (not `alternatives[].models`, which are the dissenting values). Every id must be declared in `manifest.models`
  and have `phases.propose == "ok"`; a bundle without it, or with an undeclared or failed-phase id, is refused before
  any write. It is never back-filled from alternatives.
- Per-candidate semantic rules: `council.band` must equal the band recomputed from `council.agreement` (`≥0.75`
  `alta`, `≥0.50` `media`; below `0.50` belongs in `dropped.jsonl` and is refused), each sense needs 2–4 `gloss` and
  1–3 `example` alternatives and at most 2 each of `lemma`/`regional_variant`/`register_variant`, `support` must equal
  `models.length`, `(field, sense, es, en)` must be unique, and a `target_key` must resolve to a word in the bundle or
  in the database's whole `words.key` set (all statuses).
- `dropped.jsonl` is audit only: its count is not cross-checked and none of its rows are imported. `raw/` (provider
  responses) is never read and must not be committed.
- The bundle is refused **whole** — nothing written, `run_id` not recorded — on: missing/invalid manifest,
  `status:"failed"`, missing `candidates.jsonl`, `candidates_sha256` mismatch, zero candidates, any schema or semantic
  violation, a rework candidate whose key does not exist, or a rework candidate older than the word's newest reviewer
  `edit`/`approve` revision without `--force`.

#### What it writes

- `council_runs`: one row per bundle (`run_id` unique, mode, prompt versions, style-guide hash, request topic/CEFR/count,
  requested and succeeded model counts, reduced-confidence flag, candidate/dropped counts, `candidates_sha256`,
  created/imported timestamps).
- `council_model_results`: one `propose` and one `critique` row per roster model, with per-phase status and latency;
  token and cost figures are per model, so they are written on the `propose` row only (no double count).
- `word_provenance`: one `proposed` row per id in `council.proposed_by`, plus one `critique_ok` / `critique_issue` row
  per critique entry (`issues` as JSON when the verdict is `issue`), each at the written `word_version` and run.
- `words` / `senses` / `word_alternatives` / `word_topics` / `word_revisions`: `source='council'`, `council_run_id`,
  `council_agreement`, `council_band`, `reduced_confidence`, `lint_warnings`, `provenance_method='council'`,
  `provenance_run=<run_id>`; one `action='import'` revision at the written version. Alternatives carry
  `field` and `kind` = the ΔF field name, `support`, `models` (JSON), `region`, `register` and `word_version`; an
  `example` alternative stores its Spanish sentence in `example` and its English in `gloss`. The revision snapshot is
  the candidate mapped into the corpus document shape (not the raw candidate line), because the reviewer state machine
  reads `senses[].gloss`/`acceptedEs`, `region` and `topics` from it.
- `propose` lands every new word in `pending_review`; `--hold` lands it in `draft` instead. `--hold` affects `propose`
  only. A key that already exists is skipped whatever its status (`skipped_existing`), but the run is still recorded,
  so the bundle is not retried.
- `rework` bumps `version` from the database's current version (never from a bundle number), sets `pending_review`,
  demotes cards (`epoch` + 1, `suspended_reason='word_not_approved'`, FSRS state untouched) and appends the new
  revision; the previous snapshot stays readable at its old version. `--force` overrides the newer-reviewer-verdict
  refusal for the **whole** bundle.

#### Idempotency and the content hash

- `council_runs.run_id` is unique. A bundle whose run id is already recorded is a strict no-op
  (`already imported`); the run-id check and every write share one SQLite `BEGIN IMMEDIATE` transaction, so the startup
  sync and an operator CLI cannot both import the same bundle.
- `words.content_hash` for a council word is the canonical JSON of the candidate **without `council.run_id`** (run
  identity is provenance, not source content; without the exclusion the §3.3(1) `unchanged` case is unreachable,
  because a rework run always carries a new id). Set-valued arrays — `council.proposed_by` and each
  `alternatives[].models` — are sorted ordinally before hashing, so an order-only permutation is not a content change.
  Everything else is hashed in document order.
- A `rework` candidate whose hash equals the stored `content_hash` is `unchanged`: no version bump, status change,
  revision, card epoch or alternative replacement. A reviewer's `approve_edit`/`approve_alt` never rewrites
  `content_hash`, so the comparison still detects a real source change.
- `--dry-run` validates and prints predicted counts through a read-only connection; with no database file it creates
  none and every valid candidate is predicted as `created`, otherwise the same decision code predicts
  `created`/`updated`/`unchanged`/`skipped_existing`/`skipped_reviewer_edit_newer`.

#### Startup sync

`ContentSyncService` scans `Content:CouncilDirectory` (default `content/council` under the app directory) once at
startup and imports every `*/manifest.json` with `ProposeOnly`: a `rework` bundle is refused on this path and only
logged.
`rework` reaches the database solely through `import-council` (Q-M3-7), because a container restart must never move
approved content. `Content:CouncilSyncEnabled` (env `Content__CouncilSyncEnabled`) is the kill switch, default true;
a missing directory is a successful no-op. Each bundle has its own transaction; a refusal or error is logged and
skipped, retried on the next start (its run id was never recorded), and `/healthz` is unaffected. The image ships no
bundles yet, so today this is the no-op path.

### `content approve <key…>` / `content approve --all-pending`

Originally the owner's approval path ahead of the M4 reviewer UI (locked 2026-10-18, plan O-2). Since trust-by-default
(`7234a15`) a fresh `import` approves its words and creates their cards itself, so `content approve` is **not part of
loading content**. It stays as a repair and legacy tool:

- **One-time step on an existing deployment.** Rows that were `pending_review` before the upgrade stay pending: no
  migration or startup code flips them, because the code cannot tell whether the operator meant them to wait. To apply
  the new policy to them, run once after deploying the build:

  ```sh
  docker exec palabras /app/Palabras content approve --all-pending
  ```

  It approves every `pending_review` word, including council proposals and words demoted by a re-import or a
  reviewer — check `content status` and the reviewer's `/revisar` queue first if any of those should keep waiting.
- **Card repair** after the owner account was created later than the import (cards exist only for learner accounts
  present at approval or import time): `content approve <key…>` (below).
- **Lifting a CLI or reviewer rejection** by key: approving a `rejected` word restarts its suspended cards fresh, as
  the reviewer API's undo does (`09-review-api.md`, "Objection and its undo").

- `<key…>` approves the named words, whatever their current status. One unknown key fails the whole command:
  `Unknown word key(s): … No words approved.`, exit `1`. Already approved words count as `unchanged` but still get the
  card pass.
- `--all-pending` approves every `pending_review` word. `draft` words are not included. It also re-runs card
  creation for all already-approved words, which back-fills cards for words approved before `SrsCards` existed or
  before the owner account existed.
- Per approved word it writes a `word_revisions` row with `action='approve'`, the current `version`, and the
  current-version snapshot with `status` and `updatedAt` updated. `version` and `content_hash` do not change. Since
  `ReviewGate` it also sets `words.status_changed_at` and `words.approved_version`. This is the same revision row the
  M4 reviewer API writes (same code, `09-review-api.md`); the CLI sets `actor_user_id` to NULL because it has no
  authenticated user.

**What it does not audit.** No `word_reviews` verdict row is written (`reviewer_id` is NOT NULL), so a CLI approval
is invisible to verdict queries and exports; the `word_revisions` row with a NULL actor is its only trace. The same
holds for `content reject` and for the approval an import grants on arrival (the `import` revision). Keep the key
list you passed if the decision must be explainable later.

**Card creation happens on approval**, which since trust-by-default includes the approval an import grants on arrival.
For every active sense of the word and every account that has
the `learner` role (today only the owner), two cards are created: `es_en` and `en_es`, state New, due now. Existing
cards are kept: a card suspended as `word_not_approved` is released, and cards of a word approved in this run get
`epoch` + 1, so answers queued offline against the old content come back as `stale_epoch`
(`07-learn-api.md`). Their FSRS state is preserved. `en_es` cards stay out of sessions until the `es_en` sibling has
reached Review and been answered correctly on an earlier local day.

### `content approve --top N` and `learn …`

Added in T-20261019-2100 (`5b818d1`). `--top N` selects the N lowest `(cefr, difficulty, id)` words with status
`pending_review` (unranked words last) and passes them to the same approval as `<key…>`; it cannot be combined with
keys or `--all-pending`. `learn settings` is the only writer of `user_settings`; `learn seed-known` moves the top N
approved words of one user to Review. Arguments are validated before the database is opened. Operator sequence,
semantics and cautions: `10-bring-up.md` §4–§5.

Under trust-by-default a fresh import leaves no `pending_review` words, so `--top N` selects nothing there
(`approved=0`); it only matters for a database that still holds pending rows (legacy, council proposals, demotions).
`learn seed-known` is unaffected: it selects approved words, which now includes every freshly imported word.

### `content reject <key…>`

Added in `7234a15`. The operator's objection path, sharing the reviewer API's `reject` transition and card effect
(`09-review-api.md`, "Objection and its undo"): status `rejected`, a `reject` revision with a NULL actor, cards
`epoch` + 1 and suspended as `word_not_approved` unless already `manual`/`leech`; no card or `review_log` row is
deleted and no `word_reviews` row is written. Allowed from `pending_review`, `approved` and `needs_change`; an already
rejected word counts as `unchanged`. A `draft` or `retired` word, or one unknown key, fails the whole command with
nothing written. Undo: `content approve <key>` or the reviewer's **Volver a incluir**.

### `content status`

Prints the word count for each of `draft`, `pending_review`, `approved`, `needs_change`, `rejected`, `retired`
(zero included), then `total=`. Changes no content rows, but migrates the database first (rule above).

### `content retire <key…>`

Sets the named words to `retired`, writes a `word_revisions` row with `action='retire'`, and invalidates their cards
the same way a content change does (`epoch` + 1, `word_not_approved`). Unknown keys fail the whole command. Already
retired words are skipped and not counted. To bring a word back, approve it again by key.

### `restore-progress <file.json>`

Merges a JSON progress export (`GET /api/learn/export?format=json`) back into an existing database. The whole file is
validated first; the target must already exist and must match the export's account/content fingerprint. Failure prints
`Restore refused: <reason>` and exits `1` with nothing changed. Procedure, merge rules and the isolated drill:
`restore-drill.md`.
