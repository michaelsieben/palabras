# Operator CLI: content, cards and progress restore

Shipped in T-20261018-0124 (M2 content importer and owner approval, M5 card lifecycle, M10 restore). Account
commands are in `04-accounts.md`; the learner HTTP API is in `07-learn-api.md`; the backup/restore procedure is in
`restore-drill.md`.

> The parallel `Vocabulary` model and `GET /api/words` approach (remote commit `e0e7f40`, T-20261017-1516) was
> superseded by the `Content` schema documented here and removed during the T-20261018-2005 reconciliation.

## How the CLI runs

The same binary is the web app and the CLI. When the first argument is `user`, `import`, `content` or
`restore-progress`, `Program.cs:17-41` runs that verb and exits; no web host starts. Configuration comes from
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
- Every write verb runs in one SQLite transaction: it either commits fully or changes nothing.
- Ctrl-C prints `Command cancelled.` and exits `1`.
- `user`, `import` (without `--dry-run`) and all `content` verbs create the database and apply pending migrations
  first (`DatabaseLifecycle.InitializeAsync`), as the app does at startup. `import --dry-run` and `restore-progress`
  never create or migrate a database.
- These verbs have no HTTP equivalent. Anyone who can run them has full content and progress control.

Migrations in order: `InitialAccounts` (M1), `ContentSchema` (M2, `20261017234822`), `SrsCards` (M5,
`20261018000241`), in `Backend/Palabras/Persistence/Migrations/`.

## Verbs

| Verb | Touches | Idempotent | Output on success |
| --- | --- | --- | --- |
| `user …` | `users` | per sub-command, see `04-accounts.md` | per sub-command |
| `import <dir> [--dry-run]` | `words`, `senses`, `word_alternatives`, `word_topics`, `word_revisions`, card epochs/suspension | yes: re-run with the same files → all `unchanged` | `created=N updated=N unchanged=N rejected=N` |
| `content approve <key…>` | `words.status`, `word_revisions`, `cards` | yes: already approved → `unchanged` | `approved=N unchanged=N` |
| `content approve --all-pending` | same | yes | `approved=N unchanged=N` |
| `content status` | no content rows; still applies pending migrations first, like every `content` verb, so stop it with the app during an upgrade (`08-upgrade-rollback.md`) | yes | one `<status>=<count>` line per status, then `total=N` |
| `content retire <key…>` | `words.status`, `word_revisions`, `cards` | yes: already retired is skipped | `retired=N` |
| `restore-progress <file.json>` | `cards`, `review_log` of one user | yes: identical rows are skipped | `Restored N cards and M reviews for user U.` |

Unknown or malformed arguments print the verb's usage line and exit `1` before the database is opened. Keys never
start with `-`; duplicates in a key list are collapsed.

### `import <dir> [--dry-run]`

Loads every `*.json` file directly in `<dir>` (not recursive) into the content tables. The argument is a path as
the process sees it; inside the container that is a container path. The image ships only
`content/schema/word.schema.json` (`Dockerfile:13`), not `content/words/`.

1. **Validate the whole batch before any write.** Each file must parse, contain no duplicate JSON property, pass
   `content/schema/word.schema.json` (JSON Schema, format validation on), have a file name equal to `<id>.json`, and
   carry a `createdAt` that parses as a timestamp. If any file fails, every failure is printed to stderr as
   `<file>: <reason>`, nothing is written, and the exit code is `1`. An empty directory is a successful no-op.
2. **Upsert by key.** The key is the file `id` (for example `es-coger-01`). `content_hash` is the lowercase hex
   SHA-256 of a canonical JSON form: object keys sorted ordinally, string escapes decoded, and the top-level
   `status`, `review` and `updatedAt` excluded. So a status-only or timestamp-only edit is `unchanged`.
   - Same hash → `unchanged`. Nothing is touched; an approved word stays approved, and a reviewer edit survives
     (section below).
   - New key → `created`, `version` 1, one `word_revisions` row with `action='import'`.
   - Different hash → `updated`: `version` + 1, a new `import` revision, status reset to `pending_review`, and every
     card of the word is invalidated (`epoch` + 1, `suspended_reason='word_not_approved'` unless a reason is already
     set). FSRS state is kept. The word needs `content approve` again.
3. **File `status` is advisory.** On creation only `draft` is honoured; every other value, including `approved`,
   lands as `pending_review`. An updated word always becomes `pending_review`, whatever the file says.
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
- **Source changed** → `updated`, and the source wins: current content is replaced, `version` + 1, status back to
  `pending_review`, cards demoted (`epoch` + 1). Nothing historical is deleted or rewritten — the reviewer's edit
  snapshot stays at its old version, her `word_reviews` verdict still names the version she saw, and `approved_version`
  stays the last approved version (an audit fact, not approval of the new version). A reviewer action still queued
  against the old version returns `stale_version`; the queue shows the new version (`09-review-api.md`).
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
  and sends the word back to `pending_review`. The safe direction — a reviewer edit is never lost — still holds.
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

### `content approve <key…>` / `content approve --all-pending`

The owner's approval path ahead of the M4 reviewer UI (locked 2026-10-18, plan O-2). Without it every imported word
stays `pending_review`, no cards exist, and the learner queue is empty.

- `<key…>` approves the named words, whatever their current status. One unknown key fails the whole command:
  `Unknown word key(s): … No words approved.`, exit `1`.
- `--all-pending` approves every `pending_review` word. `draft` words are not included. It also re-runs card
  creation for all already-approved words, which back-fills cards for words approved before `SrsCards` existed.
- Per approved word it writes a `word_revisions` row with `action='approve'`, the current `version`, and the
  current-version snapshot with `status` and `updatedAt` updated. `version` and `content_hash` do not change. Since
  `ReviewGate` it also sets `words.status_changed_at` and `words.approved_version`. This is the same revision row the
  M4 reviewer API writes (same code, `09-review-api.md`); the CLI sets `actor_user_id` to NULL because it has no
  authenticated user, and writes no `word_reviews` verdict row.

**Card creation happens on approval, not on import.** For every active sense of the word and every account that has
the `learner` role (today only the owner), two cards are created: `es_en` and `en_es`, state New, due now. Existing
cards are kept: a card suspended as `word_not_approved` is released, and cards of a word approved in this run get
`epoch` + 1, so answers queued offline against the old content come back as `stale_epoch`
(`07-learn-api.md`). Their FSRS state is preserved. `en_es` cards stay out of sessions until the `es_en` sibling has
reached Review and been answered correctly on an earlier local day.

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
