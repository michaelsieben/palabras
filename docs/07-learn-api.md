# Learner API, backups and export (M5, M10)

Shipped in T-20261018-0124: FSRS scheduling and the `/api/learn/*` endpoints (M5), nightly backups and progress
export (M10). The frozen contract is in `.opencode/runs/T-20261018-0124/02-retention-plan.md` §4; this file
describes the shipped code. Content and card creation: `06-content-cli.md`. Auth basics: `05-auth.md`.

## Common rules

- Every route requires policy `learner` (role `learner`; the owner account has it). Reviewer → 403
  `{"error":"forbidden"}`. Anonymous → 401 `{"error":"unauthorized"}`.
- Non-GET requests need `X-Palabras: 1`, else 400 `{"error":"missing_x_palabras"}`. A body that does not bind
  (malformed JSON, wrong types) → 400 `{"error":"bad_request"}`. Unknown `/api` paths → 404 `{"error":"not_found"}`.
- The learner is always the cookie's user id. No route or body field selects a user.
- JSON is camelCase. Instants are ISO 8601 UTC. Local dates are `yyyy-MM-dd` in the learner's zone:
  `users.timezone`, or `Europe/Madrid` when NULL or unknown (an unknown zone is logged as a warning). No endpoint
  sets `users.timezone` or `user_settings` yet, so the defaults below apply.
- The day boundary is local midnight. Server code reads time only from the injected `TimeProvider`.

Defaults (`user_settings` column defaults, used when the user has no row): desired retention 0.90, 12 new cards/day,
200 reviews/day, backlog threshold 150, session target 600 s, hard stop 900 s, maximum interval 730 days, fuzz on,
learning steps 1 min / 10 min, relearning step 10 min, leech at 8 lapses, library default weights.

## Routes

| Method + route | Success | Errors |
| --- | --- | --- |
| `GET /api/learn/session/plan?maxNew=&budgetS=` | 200 plan | 400 `bad_request` (not a non-negative integer) |
| `POST /api/learn/attempts` | 200 per-item results | 400 `bad_request` |
| `POST /api/learn/cards/{id}/bury` | 204 | 404 `not_found` |
| `POST /api/learn/cards/{id}/suspend` | 204 | 404 `not_found`, 409 `not_suspendable` |
| `POST /api/learn/cards/{id}/unsuspend` | 204 | 404 `not_found`, 409 `not_suspendable` |
| `GET /api/learn/forecast` | 200 | — |
| `GET /api/learn/words?q=&topic=` | 200 | — |
| `GET /api/learn/export?format=json\|csv` | 200 file | 400 `bad_format` |

All rows also return the 401/403 above. `404 not_found` on card routes covers both a missing card and another
user's card.

### `GET /api/learn/session/plan`

Fetching a plan writes nothing. The new-card quota is consumed by the first applied review of a card, so an
abandoned plan costs nothing.

```ts
type PlanResponse = {
  sessionId: string;            // new GUID per fetch
  generatedAtUtc: string;
  localDate: string;            // learner's local today
  items: PlanItem[];            // due items first, then new
  counts: { due: number; newOffered: number; backlog: number };
  warnings: string[];           // machine keys, see below
  nextDueUtc?: string;          // only when the plan has no due items
};
type PlanItem = {
  cardId: number; cardEpoch: number; direction: 'es_en' | 'en_es';
  mode: 'recognition' | 'typed' | 'cloze'; isNew: boolean;
  word: { key: string; headword: string; article: string | null; gender: string | null; pos: string };
  sense: { gloss: string; exampleEs: string; exampleEn: string; clozeEs?: string;
           acceptedEs: string[]; acceptedEn: string[]; accentsAreThePoint: boolean };
  notes: { region?: string; register?: string };
};
```

Composition:

- **Due** cards: not New, not suspended, not buried, due by the end of the local day, word `approved`, sense still in
  the current content. An `en_es` card qualifies only once its `es_en` sibling is in Review and has an applied,
  non-Again review in the current epoch before today. Learning/Relearning first, then by due time; at most
  `max_reviews_per_day`. `counts.due` is the number of due items in the plan; `counts.backlog` is the uncapped total.
- **New** cards: `es_en` only, ordered by CEFR, word, sense order. The limit is the remaining daily quota
  (`new_per_day` minus cards first reviewed today), then:
  - without `maxNew`: 0 when `backlog` > `backlog_threshold`; otherwise only as many as fit the time budget
    (`budgetS`, else `session_target_s`, clamped to 60 s … `session_max_s`; estimated 8 s per review, 30 s per new card);
  - with `maxNew` (the "add new anyway" path): `min(maxNew, remaining quota)`. This bypasses the backlog rule and the
    budget estimate but **never** exceeds `new_per_day`.
- **Mode**: `es_en` → `recognition`; `en_es` → `cloze` when `clozeEs` exists and the card's `reps` is odd, else
  `typed`. `clozeEs` is `exampleEs` with the first whole-word, case-insensitive headword match replaced by `____`;
  it is omitted when there is no exact match (for example a conjugated verb).
- `notes.region` is the word's region scope, `notes.register` its register. Null optional fields are omitted.

**Warnings** are machine keys, never sentences. The only key today is `backlog_no_new`: the backlog rule removed all
new cards. The user-facing sentence lives in the frontend copy module `Frontend/src/copy/feedback.ts`
(kind `backlog-warning`, interpolating `counts.backlog`).

### `POST /api/learn/attempts` (offline flush)

```ts
type AttemptsRequest = { attempts: Attempt[] };   // 1..50 items
type Attempt = {
  clientAttemptId: string;   // GUID; dedupe key
  cardId: number; cardEpoch: number;   // echo the plan item
  sessionId: string | null; mode: 'recognition' | 'typed' | 'cloze'; rating: 1 | 2 | 3 | 4;
  isCorrectMeaning: boolean | null; isCorrectForm: boolean | null; overridden: boolean | null;
  feedbackKind: string | null; answerText: string | null; durationMs: number | null; reviewedAtUtc: string;
};
type AttemptsResponse = { results: {
  clientAttemptId: string;   // echoed exactly as sent
  status: 'applied' | 'duplicate' | 'stale_epoch' | 'suspended' | 'out_of_order';
  card?: { id: number; state: 0 | 1 | 2 | 3; dueUtc: string; intervalDays: number; leech: boolean };  // applied only
}[] };
```

The whole batch is rejected with 400 `bad_request` when it is empty, has more than 50 items, or any item is null,
lacks `reviewedAtUtc`, has a non-GUID `clientAttemptId`, `cardId` ≤ 0, `cardEpoch` < 0, a rating outside 1–4, or
an unknown mode. Otherwise the server clamps `reviewedAtUtc` to `[now − 24 h, now + 5 min]` and `durationMs` to
0–600000, sorts by `(cardId, reviewedAt)`, and applies everything in one transaction. Each item gets one status:

| Status | Meaning | `review_log` row |
| --- | --- | --- |
| `duplicate` | `clientAttemptId` already stored, or repeated in this batch (GUID compared case-insensitively) | none added |
| `suspended` | card unknown or not the caller's | none |
| `stale_epoch` | `cardEpoch` ≠ the card's current epoch: content changed after the plan was fetched | stored, `counts_for_srs=0` |
| `suspended` | card suspended, word not approved, or sense no longer in the content | stored, `counts_for_srs=0` |
| `out_of_order` | clamped review time is earlier than the card's last applied review | stored, `counts_for_srs=0` |
| `applied` | FSRS review applied; card state, due, reps and lapses updated | stored, `counts_for_srs=1` |

There is never a batch-level 409. The client queues attempts offline and replays them; one stale or out-of-order
item must not block the rest of the queue, and FSRS cannot be rewound to insert a late review. Every status is final
for that attempt, so the client can drop each answered item. Replaying a batch returns `duplicate` for every item
that was stored.

**Leech**: when an applied lapse brings `lapses` to `leech_lapses` (8) or more for the first time, the card gets
`is_leech=1` and `suspended_reason='leech'`, and the result carries `leech: true`.

### `POST /api/learn/cards/{id}/bury | suspend | unsuspend`

- `bury`: sets `buried_until_local_date` to local tomorrow. The plan includes a card only when that date is before
  local today, so a card buried today is also left out tomorrow and returns the day after.
- `suspend`: sets `suspended_reason='manual'` (replacing `leech`). 409 `not_suspendable` when the reason is
  `word_not_approved` or `sense_removed`: content owns those.
- `unsuspend`: clears a `manual` or `leech` reason and `is_leech`; `lapses` is kept for the optimiser. 409
  `not_suspendable` for the content-owned reasons, and when the card's sense is no longer active approved content.

### `GET /api/learn/forecast`

`{ tomorrow: number, next7Days: { date: string, due: number }[] }` for local days +1 … +7. Each count is the cards
due in that local day that would be eligible for a plan (same filters as Due above; New cards are not counted).
`tomorrow` equals `next7Days[0].due`.

### `GET /api/learn/words?q=&topic=`

`{ items: { key, article, headword, gloss, topics, stage }[] }`: approved words with at least one active sense,
ordered by CEFR then headword. `q` is a substring match on headword or key; `topic` is an exact topic. `gloss` is the
first active sense's gloss. `stage` is the caller's furthest card: `nuevo` (no card past New), `aprendiendo`,
`reciente` (Review), `consolidada` (Review, stability ≥ 21 days), `dominada` (Review, stability ≥ 90 days). No paging.

## Backups and export (M10)

- **Nightly backup.** `BackupService` (hosted in the web app, not the CLI) runs SQLite `VACUUM INTO` at **03:30 in
  the owner's local zone** (`users.timezone`, fallback `Europe/Madrid`; not the container `TZ`). Missed nights are
  not replayed. Each snapshot is written as `.partial`, checked (`PRAGMA integrity_check` plus required tables), then
  renamed. The newest **7 successful snapshots** are kept; older `palabras-*.db` files are deleted. A failure is
  logged, keeps the earlier snapshots, and the schedule retries after one hour.
- **`/data` layout** (beside the configured database, default `DATA_DIR=/data`):

  ```
  /data/palabras.db (+ -wal, -shm)   live SQLite database, WAL mode
  /data/keys/                        data-protection keys (not in backups)
  /data/backups/palabras-<UTC yyyyMMddTHHmmssfffffffZ>-<guid>.db
  ```

- **Export.** `GET /api/learn/export?format=json|csv` returns the caller's own `cards` and `review_log` only, as an
  attachment `palabras-progress.<format>` with `Cache-Control: no-store`. No passwords, settings, content or other
  users. JSON v1 carries `userId`, a target fingerprint and the export time and is the only restorable format; CSV is
  one table with a `table` column (`cards` / `review_log`) for spreadsheets.
- **Restore.** CLI `restore-progress <file.json>` (`06-content-cli.md`). Contract details, the isolated drill and the
  owner-run recovery steps are in `restore-drill.md`; follow that file rather than copying its steps.

## Deliberate deviations from the design docs

Reviewed and accepted in T-20261018-0124 (plan §10.4). `design.md` and `design-amendment.md` are older than these.

| Deviation | Design doc said | Shipped | Why |
| --- | --- | --- | --- |
| Instants as integers | `DATETIMEOFFSET` columns (ΔF) | `due_ms`, `last_review_ms`, `reviewed_at_ms`, `received_at_ms`, `due_after_ms`: UTC Unix milliseconds | EF Core's SQLite provider cannot reliably order or compare `DateTimeOffset`; the queue query must translate to SQL |
| Leech reason | `suspended_reason` ∈ `word_not_approved`, `sense_removed`, `manual` | CHECK adds `'leech'` (`SrsCards` migration) | leech auto-suspend needs its own reason so `unsuspend` and content rules can tell it apart |
| Golden-test oracle | `ts-fsrs` | py-fsrs 6.3.2 via `tools/fsrs-golden/`, output committed as `Backend/Palabras.Tests/Srs/golden.json` | Fsrs.Sharp 2.0.0 is validated against py-fsrs; a third implementation adds rounding noise |
| Missing import file | `design.md` §8: missing entries → retired | never auto-retired; explicit `content retire` | a partial directory must not retire the corpus |
| Approval before M4 | approval only in the M4 reviewer UI | owner CLI `content approve` in M2 | without it no cards exist and the learner queue stays empty (owner decision O-2) |

Fsrs.Sharp 2.0.0 is used from NuGet (pinned `[2.0.0]`): its `ReviewCard` accepts an explicit review time, so the
vendored fallback was not needed.
