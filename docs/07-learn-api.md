# Learner API, day status, backups and export (M5, M7, M10)

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
user's card. `GET /api/learn/stats?from=&to=` (M6.6) is described under "Learner stats" at the end of this file.

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

- `bury`: sets `buried_until_local_date` to local today, the **last** local date the card stays hidden. The plan
  includes a card only when that date is before local today: hidden for the rest of today, back from local midnight.
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

Reviewed and accepted in T-20261018-0124 (plan §10.4); the last two rows are owner decisions of T-20261018-2105. `design.md` and `design-amendment.md` are older than these.

| Deviation | Design doc said | Shipped | Why |
| --- | --- | --- | --- |
| Instants as integers | `DATETIMEOFFSET` columns (ΔF) | `due_ms`, `last_review_ms`, `reviewed_at_ms`, `received_at_ms`, `due_after_ms`: UTC Unix milliseconds | EF Core's SQLite provider cannot reliably order or compare `DateTimeOffset`; the queue query must translate to SQL |
| Leech reason | `suspended_reason` ∈ `word_not_approved`, `sense_removed`, `manual` | CHECK adds `'leech'` (`SrsCards` migration) | leech auto-suspend needs its own reason so `unsuspend` and content rules can tell it apart |
| Golden-test oracle | `ts-fsrs` | py-fsrs 6.3.2 via `tools/fsrs-golden/`, output committed as `Backend/Palabras.Tests/Srs/golden.json` | Fsrs.Sharp 2.0.0 is validated against py-fsrs; a third implementation adds rounding noise |
| Missing import file | `design.md` §8: missing entries → retired | never auto-retired; explicit `content retire` | a partial directory must not retire the corpus |
| Approval before M4 | approval only in the M4 reviewer UI | owner CLI `content approve` in M2 | without it no cards exist and the learner queue stays empty (owner decision O-2) |
| Exercises 1–2 (owner decision, T-20261018-2105) | `design.md` §4 types 1–2: new-word presentation + memory match, four-option multiple choice with same-POS/topic distractors | plan modes `recognition`, `typed`, `cloze` only (see `GET /api/learn/session/plan`); new cards start as `recognition` | deliberate substitution, not a gap: the shipped session contract replaces both types. ΔG M6 "exercises 1–4" now means this mode set |
| Session timer and estimate (owner decision, T-20261018-2105) | `design.md` §4: progress bar with estimated minutes, wall-clock end at 900 s; frozen M4 acceptance (`runs/01-spanish-pwa-design.md:408`): estimate within ±20 % of actual over 3 sessions | composer budgeting only (`budgetS` / `session_target_s`, 8 s per review, 30 s per new card); the plan carries no duration estimate and the client does not end the session at 900 s | the ±20 % estimate criterion is **dropped**, not deferred: there is no estimate to measure. The rest of that acceptance (a 10-minute session completes in airplane mode and syncs) still applies |

Fsrs.Sharp 2.0.0 is used from NuGet (pinned `[2.0.0]`): its `ReviewCard` accepts an explicit review time, so the
vendored fallback was not needed.

## Known limitations and open items

Recorded from the T-20261018-0124 reviews. None is fixed by this file; each states what holds today.

- **Settings defaults are duplicated.** The same values live in the `UserSettings` property initialisers
  (`Backend/Palabras/Persistence/UserSettings.cs`) and the column defaults (`PalabrasDbContext.cs:133-145`, mirrored
  in the `SrsCards` migration); `LearnSettings.cs:19-20` repeats the step fallbacks. They match today, but no test
  binds them, so an edit to one copy can diverge silently. Nothing writes `user_settings` yet. The first feature
  that does must add that binding test and harden the parse path below.
- **Some stored values throw instead of falling back.** `learning_steps` or `relearning_steps` of `"[null]"`
  (`ArgumentNullException`, not caught in `LearnSettings.cs:45-56`), a wrong-length `weights_json` such as `"[123]"`
  (`ArgumentException` from the FSRS library), and a blank or whitespace-only `users.timezone` (skips the
  unknown-zone guard in `LearnEndpoints.cs:282`, then fails the zone lookup) make the affected learner requests
  return 500. The NULL/unknown fallback under Common rules does not cover these. Unreachable today: no code path
  writes `user_settings` or `users.timezone`.
- **`content status` migrates.** Like every `content` verb it applies pending migrations first
  (`06-content-cli.md`, CLI rules). The CLI from a newer clone is therefore not a read-only probe: run against a live
  pre-upgrade database, it upgrades it. See `08-upgrade-rollback.md`.
- **Floating Docker base tags.** `Dockerfile:1,8,17` use `node:24-bookworm-slim`, `dotnet/sdk:10.0` and
  `dotnet/aspnet:10.0`. A clean rebuild of the same commit can produce a different runtime. The only exact rollback
  target is a retained image ID (`08-upgrade-rollback.md`).
- **Disk growth has no coded quota.** Peak use is the live database plus seven snapshots plus the snapshot being
  written plus WAL headroom; a SIGKILL during a backup leaves a `.partial` file that pruning ignores. Accepted for
  one learner and one nightly backup. Monitor free space and the age of the newest successful snapshot.
- **Overlapping attempts.** Release-risk item B3 (two concurrent `POST /api/learn/attempts` for the same card can
  lose card state) is being fixed in a separate lane. This file does not describe that change.
- **CI fails and is deferred** by owner decision. The local verify set in `AGENTS.md` is authoritative until CI is
  green.

## Learner stats (M6.6)

Shipped in T-20261018-0332 (`545ed2b`). Design: `.opencode/runs/T-20261018-0332/04-stats-design.md` §1/§3, with the
correction note at its end. Code: `Backend/Palabras/Learn/StatsEndpoints.cs`, `LearnStats.cs`, `StatsContracts.cs`.
Placed here, after the M5/M10 sections, so existing line references into this file stay valid.

| Method + route | Success | Errors |
| --- | --- | --- |
| `GET /api/learn/stats?from=&to=` | 200 stats | 400 `bad_request` |

- **Auth:** policy `learner`; the user id comes from the cookie only (Common rules). Reviewer → 403, anonymous → 401.
  GET, so no `X-Palabras` header.
- **Read-only, online-only.** Nothing is written. No new table, column or migration: every figure is counted from
  existing `review_log` and `cards` rows. Stats is online-only (owner item O-17): no offline cache or queue.
- **Owner items decided by the orchestrator** (owner did not answer): O-13, build now as a read-only surface with no
  migration, ahead of the M6.5 `practice` column; O-14, see "hit" below.

### Query parameters

- `from`, `to`: inclusive local dates `yyyy-MM-dd` in the learner's zone. Defaults: `to` = local today,
  `from` = `to − 29` (30 days). A blank value (`?from=&to=`) counts as absent.
- 400 `{"error":"bad_request"}` when either value is present but not an exact `yyyy-MM-dd` date, `from` > `to`
  (inverted range), `to` is after local today, or the range spans more than 366 days (366 is accepted).
- The zone falls back as under Common rules. A blank `users.timezone` returns 500 here too: `StatsEndpoints.cs` copies
  the `CreateClock` fallback from `LearnEndpoints.cs` (see Known limitations; still unreachable today).

### Response

```ts
type StatsResponse = {
  localDate: string; from: string; to: string;
  totals: { answers: number; hits: number; misses: number; formSlips: number; overridden: number;
            firstTry: number; secondLook: number; answerMs: number; activeDays: number; newWords: number };
  days: { date: string; answers: number; hits: number; misses: number; newWords: number; answerMs: number }[];
  byMode: { mode: string; answers: number; hits: number; misses: number; formSlips: number }[];
  retention: { target: number;
               all:  { reviews: number; passed: number };
               esEn: { reviews: number; passed: number };
               enEs: { reviews: number; passed: number } };
  words: { approved: number;
           stages: { nuevo: number; aprendiendo: number; reciente: number; consolidada: number; dominada: number };
           leechCards: number; suspendedCards: { manual: number; leech: number } };
  hardest: { key: string; article: string | null; headword: string; gloss: string; misses: number }[];
  quality: { lateRows: number; missingDurationRows: number };
};
```

Range figures (`totals`, `days`, `byMode`, `retention`, `hardest`, `quality`) use rows whose `reviewed_at_ms` falls in
`[start of from, end of to]` local time. `words` is a snapshot of current state, not a range figure.

- **`totals`, `days`.** `answers` counts every stored row, including rejected ones (`counts_for_srs = 0`:
  `stale_epoch`, `suspended`, `out_of_order`), because the learner did answer them. Requeue repeats count too.
  - **Hit** (O-14): any rating above Again (Hard, Good, Easy). **Miss**: rating Again. `hits + misses = answers`.
  - `overridden`: rows the learner self-overrode. They are already in `hits`; the separate count shows how much of the
    total they make up.
  - `formSlips`: hits with `is_correct_form = 0` (missing article, wrong gender, accent near-miss). Typed and cloze only.
  - `firstTry` / `secondLook` classify the **first** answer of each `(session_id, card_id)` group, the same rule as
    the session summary: first row rated above Again → `firstTry`, rated Again → `secondLook`. The first row is the
    lowest `reviewed_at_ms`, ties broken by row id. A row with a NULL `session_id` is its own group. So
    `firstTry + secondLook` is the number of groups, not `answers`.
  - Groups and `answers` are both limited to the requested range: a session that straddles `from` counts its first
    **in-range** row as its first answer.
  - `activeDays`: local dates in range with at least one answer. Not a streak (streaks are M7).
  - `newWords`: `es_en` cards whose `introduced_local_date` is in range, the same rule as the daily new-word budget.
    An `en_es` unlock is the same word and is not counted. An introduction alone does not make an active day.
  - `days` is dense: one entry per local date `from..to`, zeros included, one bucket per date across DST changes.
- **`byMode`.** One entry per stored `mode` string present in range, ordered by name. Unknown future modes pass
  through. This is where machine-checked answers (`typed`, `cloze`: `en_es`) are reported separately from self-graded
  recognition (`recognition`: `es_en`).
- **`retention`.** Applied rows only (`counts_for_srs = 1`) whose `state_before` is Review: `passed` = rated above
  Again. `esEn` is self-graded, `enEs` is checked. No percentage is sent; the client computes the ratio and hides it
  below 20 reviews. `target` is `user_settings.desired_retention` (0.90 default). It is a pass rate, not proof of
  recall. `all.reviews` can exceed `esEn.reviews + enEs.reviews` only for a row whose card no longer exists.
- **`words`.** Approved words with at least one active sense, as in `GET /api/learn/words`. `stages` uses the same
  furthest-card rule as the `stage` field there, so the two always agree. `leechCards` and `suspendedCards` count
  cards, not words, and only the learner-owned reasons `manual` and `leech`.
- **`hardest`.** Up to 5 approved words with the most misses in range, ordered by misses, then headword, then word id.
  A word no longer approved stays in the totals but is never named.
- **`quality.lateRows`**: rows with `received_at_ms − reviewed_at_ms` > **23 h**. `missingDurationRows`: rows with
  no `duration_ms`.

### Estimates and thresholds

Every count above is exact over the stored rows. Two figures are estimates:

- **Answer time.** `answerMs` sums the client-reported `duration_ms`: prompt to "Mostrar respuesta" for recognition,
  prompt to submit for typed and cloze. Feedback reading and rating are excluded, each row is capped at 600 s
  (600 000 ms), and a NULL adds nothing. It is a lower bound of time in the app; label it "tiempo respondiendo".
  Real time in the app arrives with M7's daily record.
- **Day bucket of a late flush.** The attempts endpoint clamps `reviewedAtUtc` to `[now − 24 h, now + 5 min]` at
  arrival (see `POST /api/learn/attempts`). An offline answer flushed more than 24 h after it was given is stored at
  arrival − 24 h and counted on that local day, not the day it was answered.

**Why the late-flush threshold is 23 h, not 24 h.** 24 h is the clamp bound, not the detection threshold.
`received_at_ms` is read before the clamp, so a clamped row has `received − reviewed = 24 h − processing time`, a
little under 24 h. A strict `> 24 h` test would miss exactly the rows the counter exists to flag. With 23 h every
clamped row is counted, plus any row flushed 23–24 h late that was bucketed correctly. The count over-reports slightly
by design: it only triggers a footnote and changes no other figure.

## Day status, streak and skip (M7)

Backend shipped in T-20261018-2005 (`83d29a5`); rollover gate, backfill guard and `localDate` on `done` added in
T-20261018-2105 (`3a6b4c9`); durable cutover (`c7262f8`) and owner-only history (`c3ce543`) in the same run. No
frontend caller yet. Spec: `design-amendment.md` ΔD (skip, refund,
rollover) and ΔF (schema). Code: `Backend/Palabras/Learn/DayEndpoints.cs`, `DayContracts.cs`, `LearnDayStatus.cs`,
`DayRollover.cs`, `DayRolloverService.cs`, `DayRolloverSettings.cs`; migrations `20261018183151_DayStatusAndSkips`
(tables `day_status`, `day_skips`, `practice_sessions`) and `20261018201418_RolloverCutoverMarker` (table
`app_meta`). Open items O-M7-n: `.opencode/runs/T-20261018-2005/10-m7-backend.md` §5. Placed at the end so
existing line references into this file stay valid.

| Method + route | Success | Errors |
| --- | --- | --- |
| `GET /api/learn/day/status` | 200 day status | — |
| `POST /api/learn/day/skip` | 200 day status (`skipped`) | 400 `bad_request`, 409 `skip_unavailable`, 409 `already_done` |
| `POST /api/learn/day/done` | 200 day status (`done`) | 400 `bad_request` |

All rows also return the 401/403 of Common rules; the two POSTs need `X-Palabras: 1`. "Today" is the learner's local
date under Common rules, read from the server clock. The client chooses the day it writes only through the bounded
`localDate` on `done` (today or yesterday, see below); skip is today-only.

ΔD fixes only the skip route. The status route and its shape (O-M7-1) and the `done` completion signal (O-M7-2) are
not in the design docs: they are the step-10 implementation choices, recorded here as shipped, not as reviewed design.

### Day status payload

All three routes answer with this shape. Nulls are sent, not omitted.

```ts
type DayStatusResponse = {
  localDate: string;                               // learner's local today; for `done`, the target day
  status: 'done' | 'skipped' | 'missed' | null;    // that day's day_status row; null when there is none yet
  streak: number;
  skipAvailable: boolean;
  skipNextAvailable: string | null;                // first local date a new skip is allowed; null while available
};
```

- **Streak**: consecutive local days with `done` or `skipped`, walked back from today. `done` adds 1; `skipped` keeps
  the chain but adds 0 (ΔD:290). `missed` or a day with no row ends it. When today has no row yet the walk starts at
  yesterday, so the streak shown before today's session is yesterday's chain (O-M7-4). The ISO-week clause of
  `design.md` §3 is not applied (ΔD:292).
- **Skip window**: a skip is available iff no non-refunded `day_skips` row falls in `[today − 6, today]`;
  `skipNextAvailable` = latest such row + 7 days (ΔD:282-283).

### `POST /api/learn/day/skip`

```ts
type SkipRequest = { localDate: string; source: 'app' | 'notification' };
```

- 200: today becomes `skipped` and one `day_skips` row is written. Response: `skipAvailable: false`,
  `skipNextAvailable` = today + 7.
- 200, idempotent: today is already `skipped`; no new ledger row.
- 409 `{"reason":"already_done"}`: today is `done`.
- 409 `{"reason":"skip_unavailable","nextAvailable":"yyyy-MM-dd"}`: a skip is already consumed in the window.
- 400 `{"error":"bad_request"}`: no body, `source` not `app`/`notification`, `localDate` not exactly `yyyy-MM-dd`, or
  `localDate` not the learner's local today (ΔD:281, so a stale notification tap after midnight cannot spend a skip).

The window check and the insert run in one immediate SQLite transaction (`BEGIN IMMEDIATE`), so a double tap cannot
consume two skips; the unique `(user_id, local_date)` index is the backstop. A skip writes no `review_log` row and
changes no card (ΔD:299).

Decided (O-M7-5, T-20261018-2105): ΔD:288 sketches the 409 body as snake_case `{reason, next_available}`; the
shipped body stays camelCase `nextAvailable`. The API-wide rule is camelCase (Common rules) and ΔD:288 is the outlier,
so this is deliberate, not a bug to fix; no snake_case alias ships without a consumer that needs one. Changing it
would be a wire change for the owner to decide. Not settled: the 400 for a non-today date has no code in ΔD (O-M7-6).
An M8 service worker should branch on the status code only.

### `POST /api/learn/day/done`

```ts
type DayDoneRequest = {
  durationS?: number; plannedS?: number; newCount?: number; reviewCount?: number;   // all ≥ 0
  localDate?: string;   // target day, yyyy-MM-dd; omitted or null = the receipt day (learner's local today)
};
```

The **target day** is `localDate` when sent, else the learner's local today at receipt. Accepted: learner-local
today or yesterday, and not before the account's local creation date (O-M7-9, `3a6b4c9`).

- 200: the target day becomes `done`, one `practice_sessions` row (`status='completed'`, `local_date` = target,
  `started_at` = `completed_at` = now, because the endpoint does not know the start) is written and linked from
  `day_status.session_id`. A non-refunded skip on the **target day** is refunded (`refunded_at` set), so the token is
  available again (ΔD:293). A skip on another day stays consumed. A `missed` row on the target day (from the rollover,
  or after a zone change) is overwritten with `done` (O-M7-7). The payload describes the **target day**: `localDate`
  is the target, the streak is the chain through it, skip fields are evaluated at it. A caller showing today should
  refetch `GET /day/status`.
- 200, idempotent: the target day is already `done`; no second session row.
- 400 `{"error":"bad_request"}`, nothing written: no body; a negative value; `localDate` present but not exactly
  `yyyy-MM-dd` (after trimming; an empty string is not "absent"); `localDate` in the future, before yesterday, or
  before the account's creation day. `{}` is accepted and completes the receipt day.

What this means for a caller:

- An offline completion flushed after local midnight with `localDate` set to the session's day is attributed to that
  day, not the new one, as long as it arrives by the end of the next local day.
- A `done` that arrives after midnight marks the **target** day. Without `localDate` the target is the receipt day, so
  an old-style call after midnight still marks the new day done and leaves the session day without `done`.
- A retry later than the window (two or more local days after the session day) gets 400 and completes nothing; it
  never falls back to today. That session day stays without `done`.

Not settled:

- **Client-trusted** (O-M7-10): the server does not check that the composed session was finished or that ≥ 300 s
  were logged (`design.md:171`). The authenticated client decides when to call it, and which of the two days it names.

### Rollover and missed days

**Gate, default OFF** (`3a6b4c9`). The rollover runs only when the setting `M7:DayRolloverEnabled` (container
environment variable `M7__DayRolloverEnabled`) is the literal `true` (case-insensitive, trimmed). Absent, empty,
`false`, `1`, `yes` or anything else keeps it off. With it off the service logs one line at startup and schedules no
tick: no `missed` history is written and the database is not touched by the job (it neither reads nor writes the
cutover marker below). The schema migrates regardless: `DayStatusAndSkips` and `RolloverCutoverMarker` are applied at
startup whether the gate is on or off, and the three day routes stay callable.

This setting is the kill switch for the one M7 behaviour that writes history nobody asked for. Turning it off (set
`false` or remove it, then restart/recreate the container; no rebuild, no database change) stops future ticks; rows
already written stay. Enabling it is a separate, deliberate decision, not part of shipping the schema, and it remains
gated on the outstanding rollout-risk conditions (truthful job evidence, completion-capable client; see "Not
covered yet") in `.opencode/runs/T-20261018-2105/01-m7-rollout-risk.md` C4–C6. Owner procedure and the observation that
proves it is off: `08-upgrade-rollback.md` §D. As of `c3ce543` the rollover is **not enabled** anywhere this repo
records.

**Durable cutover (backfill guard).** The first time a host observes the gate enabled, it fixes the cutover to that
instant and persists it in the `app_meta(key, value)` table under key `m7_rollover_cutover_ms` (UTC unix ms as a
decimal string; `DayRollover.CutoverKey`). Every later tick, restart, container recreate, poll redeploy or re-enable
reads that same instant back, so the cutover never moves (`c7262f8`; this supersedes the earlier host-start
`TrackingStart`, which was not persisted). A candidate day whose learner-local start is before the cutover is refused
and never written later. So the enable day is never marked `missed`, and the first day that can become `missed` is the
first full local day after the cutover, written on a tick after 00:05 of the following day. Operationally:

- Enabling the gate on a long-running deployment does not retro-fill: no day before the cutover gets a row.
- A restart or redeploy after enablement keeps the cutover, so the restart day is still judged the next day.
- Kill switch off and on again: the original cutover is kept, not re-anchored. Only yesterday is ever inspected, so
  days inside the off window other than the re-enable tick's yesterday are never backfilled.
- A marker that is unreadable (hand-edited, truncated, negative or outside the `DateTimeOffset` range) is logged as a
  warning and re-anchored to now through a conditional write; re-anchoring only narrows the tracked span and never
  invents history. Two instances racing on the first or a corrupt marker converge on one stored instant.
- A failed cutover resolution is not cached; the next minute tick retries without a restart (`c3ce543`).
- Restoring an older whole-file backup restores that backup's own marker with it.

**Eligibility: owner account only** (C4, `c3ce543`). Only the account with `users.role == "owner"` (code constant
`DayRollover.LearnerRole`) receives `missed` history: it is the only role admitted to the `learner` policy
(`Auth/AccountAuthentication.cs:45`), and `ck_users_role` plus the unique role index allow at most one owner. The
reviewer account never gets rows: it has no day-completion signal. One row per missed day, not one per account.

When enabled, `DayRolloverService` (hosted in the web app) ticks every minute; the first tick is one minute after
start. For the owner account, once local time is 00:05 or later, it writes `missed` for
**yesterday** when yesterday has no `day_status` row and began at or after the cutover (ΔD:297). It never overwrites `done` or `skipped`, never consumes a skip, and
touches no card or `review_log` row (ΔD:298-300). A day before the account's creation date is never marked (O-M7-8).
It is idempotent (existence check plus primary key); a failed tick is logged and retried on the next tick.

- Only yesterday is written. After an outage longer than a day, earlier days keep no row. The streak treats a day
  with no row like `missed`, so the streak is unaffected, but history has gaps.
- Between local midnight and 00:05 yesterday may still have no row; the status route does not run the rollover.

### Not covered yet

- ΔD test "timezone change applies from the next day": no endpoint sets `users.timezone`, so it is untested.
- M7 frontend (status strip, "Saltar hoy" button, `done` call from the session summary) and the M8 notification
  action are not built.
- The rollover is not enabled (gate above). Of its enablement preconditions: C4's backend part is implemented
  (owner-only eligibility and a durable cutover, `c7262f8`/`c3ce543`), but enablement still waits for an
  M7-capable client deployed to the owner; C6 is partial (success is logged only after the commit, `c3ce543`, but no
  committed-tick or failure signal is exposed and `/healthz` stays green on job errors); C5's backend part
  (`localDate` on `done`) is implemented, its client is not built.
- A cutover marker deleted while the gate is on is not re-read by the running process until a restart (review 17
  F4, informational; `.opencode/runs/T-20261018-2105/18-rollover-fixes.md` §6).
