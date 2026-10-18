# Operator CLI: content, cards and progress restore

Shipped in T-20261018-0124 (M2 content importer and owner approval, M5 card lifecycle, M10 restore). Account
commands are in `04-accounts.md`; the learner HTTP API is in `07-learn-api.md`; the backup/restore procedure is in
`restore-drill.md`.

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
   - Same hash → `unchanged`. Nothing is touched; an approved word stays approved.
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
  current-version snapshot with `status` and `updatedAt` updated. `version` and `content_hash` do not change. This is
  the same revision row the M4 reviewer UI is to write; the CLI sets `actor_user_id` to NULL because it has no
  authenticated user.

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
