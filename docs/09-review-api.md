# Reviewer API (M4)

Shipped in T-20261019-1606: the reviewer gate over `word_reviews` (`3565e75` schema, `e75b850` endpoints,
`bb1a015` approved-list `version`). Code: `Backend/Palabras/Review/ReviewEndpoints.cs` (routes),
`ReviewContracts.cs` (wire shapes), `ReviewTransitions.cs` (state machine shared with the CLI). The plan is
`.opencode/runs/T-20261019-1508/10-m4-plan.md` §2; this file describes the shipped code. The reviewer UI (`/revisar`,
`/aprobadas`) is not covered here — its status and open acceptance items are in `00-handoff.md` §7 and
`01-next-steps.md` Status. CLI approval: `06-content-cli.md`. Learner API conventions: `07-learn-api.md`.
Migration and rollback: `08-upgrade-rollback.md` section G (`ReviewGate`) and section H (`CouncilImport`,
`WordDifficulty`).

## Review policy: trust by default

Since run `T-20261019-2100` (`7234a15`, `37e1371`, `675a968`, `be76f20`) a corpus import (`import`) lands every word
`approved` with its cards created in the same transaction, unless the source file says `status: "draft"`. The
reviewer queue is where the reviewer **objects**: `reject` withdraws an approved word, `approve` lifts the objection,
`approve_edit`/`approve_alt` correct an approved word in place, and `demote` sends it back to `pending_review`. The
`pending` scope still exists for words that need a decision: council proposals (`import-council` writes
`pending_review`, or `draft` with `--hold`), changed-source re-imports of words the reviewer edited (below), demoted
words and rows from before this policy. No migration was added; existing `pending_review` rows are not flipped by the
upgrade (operator step: `06-content-cli.md`, "content approve").

## Common rules

- Every route requires policy `reviewer` (role `reviewer`). A `reviewer` account exists only if one was created with
  `docker exec -it palabras /app/Palabras user create reviewer --username <name>` (`04-accounts.md:9`); nothing seeds
  it, and one cookie session cannot hold both roles, so learner and reviewer are two accounts. The owner account is
  refused by design — it has no reviewer role, so the owner → 403 `{"error":"forbidden"}`; anonymous → 401
  `{"error":"unauthorized"}` (`Review/ReviewEndpoints.cs:12-13`, `M4ReviewApiTests.cs:948-953`). The reviewer gets
  403 on `/api/learn/*` (`07-learn-api.md`).
- `POST /api/review/actions` needs `X-Palabras: 1`, else 400 `{"error":"missing_x_palabras"}`. The two GET routes do
  not. A body that does not bind → 400 `{"error":"bad_request"}`.
- The reviewer is always the cookie's user id; no body field names a reviewer.
- JSON is camelCase. Optional members are omitted, not sent as `null`. Instants are ISO 8601 UTC.

## Routes

| Method + route | Success | Errors |
| --- | --- | --- |
| `GET /api/review/queue?status=&limit=&after=` | 200 queue page | 400 `bad_request` (`limit` not an integer in 1–30, unknown `status`, bad or stale `after`) |
| `POST /api/review/actions` | 200 per-item results | 400 `bad_request` (batch shape), 500 (corrupt word, see limitations) |
| `GET /api/review/approved?q=&after=&limit=` | 200 page | 400 `bad_request` (`limit` not 1–50, bad or stale `after`) |

All rows also return the 401/403 above.

### `GET /api/review/queue`

Returns one page (`limit` default 30, maximum 30) of the review queue. Query parameters:

| `status` | Rows | Paging |
| --- | --- | --- |
| absent or `pending` | `pending_review` only | first `limit` rows; `after` is parsed but ignored; no `nextAfter` |
| `approved` | `approved` only | decided-list cursor |
| `rejected` | `rejected` only | decided-list cursor |
| `all` | union of the three | pending block, then decided list (below) |

Any other value, including `ALL` or `draft`, is 400 `bad_request`. The scopes are mutually exclusive; `all` is their
union. `needs_change`, `draft` and `retired` words are in no scope.

**No `status` means pending only** (`ReviewEndpoints.TryQueueScope`, `37e1371`). Between `7234a15` and `37e1371` the
default was `all`; a client written against that interim default now silently receives fewer rows and must send
`status=all` (or `approved`/`rejected`) explicitly. The shipped reviewer UI sends an explicit `status` on every queue
request (`Frontend/src/api/review.ts`).

```ts
{ items: ReviewItem[]; pendingTotal: number; topics: { topic: string; pending: number }[];
  approvedTotal: number; nextAfter?: string; rejectedTotal: number }

ReviewItem = {
  wordId: number; key: string; version: number; headword: string; article?: string; gender?: string;
  pos: string; cefr: string; topic?: string;
  senses: { order: number; gloss: string; acceptedEn: string[]; acceptedEs: string[];
            exampleEs: string; exampleEn: string; notes?: string }[];
  notes?: string; regionNotes?: string; registerNotes?: string;
  agreement?: number; band?: string; reducedConfidence: boolean; lintWarnings: string[];
  alternatives: { id: number; field: string; text: string; translation?: string; reason: string;
                  support?: string /* "<support>/<models_requested>", e.g. "2/4" */ }[];
  lastFlag?: { action: "needs_change"; note?: string; at: string };
  status: "pending_review" | "approved" | "rejected";
}
```

- `pendingTotal` counts every pending word, not only the page, and is 0 for `status=approved`/`rejected`. `topics`
  lists pending counts per topic, largest first, then by name; words without a topic are not listed there; it is
  empty for scopes without pending rows.
- `approvedTotal` and `rejectedTotal` count every approved / rejected word **in the selected scope** (0 when the scope
  excludes that status), not only the page.
- `status` on every item tells a pending row from a decided one. Render only `pending_review` rows as pending cards:
  `approve` on an `approved` row is `invalid_transition`.
- Pending order: the topic with the most pending words first; inside a topic `agreement` descending with missing
  values last; then `key` (ordinal). A word's topic is `topics[0]` of its current-version snapshot, else the
  alphabetically first `word_topics` row.
- Decided order (`approved` and `rejected` rows, one shared list): decision time descending, then `wordId`
  descending. Decision time is `status_changed_at`, else the newest `approve` or `reject` revision, else
  `words.updated_at`, else `created_at` — the `approvedAt` rule of `GET /api/review/approved`, extended to `reject`.

#### Queue cursor

`nextAfter` is an opaque cursor string: `p:<wordId>` for the pending block, `d:<wordId>` for the
decided list. It is present only while rows remain. Send it back as `after` with the same `status`,
unchanged: do not parse it, and never construct one. Walking `after=nextAfter` until `nextAfter` is
absent returns every row of the scope exactly once for a corpus that does not change during the
walk (`M4ReviewApiTests.QueueAllScope*`).

- `approved` / `rejected`: only a `d:` cursor is valid; it must name a word in that scope's decided
  list, and the page continues after it. A `p:` cursor on these scopes is 400 `bad_request`.
- `all`: one sequence — the pending block first, then the decided list in `(DecidedAt, Id)` descending
  order. A `p:` cursor resumes the pending block after that row (needed when pending exceeds `limit`),
  and the decided list then starts from its head with the remaining budget. A `d:` cursor skips the
  pending block. When the pending block fills the page exactly and decided rows remain, the page
  still carries a `p:` cursor (its last pending id), handing off to the decided list on the next request.
- Stale cursor: a `d:` cursor for a word that is not in a nonempty decided list (for example it became
  `needs_change`), and a `p:` cursor for a word that is no longer pending (the reviewer approved or
  rejected the last row of the previous page), are 400 `bad_request`. The client restarts at page
  one without `after`. In `status=all`, this restart loses no rows: the acted-on row has moved to the
  decided list, and the remaining pending rows are read again. A `d:` cursor for a word that is still
  pending is also 400 when the decided list is nonempty. An `after` that is not `p:<positive integer>`
  or `d:<positive integer>` is 400 in every scope; bare numeric ids, unknown or uppercase prefixes,
  and malformed cursors are no longer accepted. An empty decided list returns an empty page even
  for a syntactically valid stale `d:` cursor.
- For paged scopes (`approved`, `rejected`, `all`), `nextAfter: null` internally means both selected
  lists are exhausted; the wire omits the null member (`ReviewQueueResponse`). `status=pending` keeps
  its single-page contract: valid cursors are ignored and no `nextAfter` is emitted, even if more pending rows exist.
- Clients that decide rows while walking `status=all` restart at page one after acting.
- Pending order is recomputed per request from the current pending set (topic counts), so deciding a pending row can
  reorder the rest; a pending cursor can then repeat or skip a row when pending exceeds `limit`. Same remedy:
  restart after acting.
- `senses` are the active senses of the current version (`sense_order` below the snapshot's sense count).
- `alternatives` is the chip list. A stored `word_alternatives.field` (council rows since M3b) wins over the legacy
  `kind` vocabulary; `kind` resolves as `synonym`, `near-synonym`, `different-sense` → `lemma`; `regional` →
  `regional_variant`; `register` → `register_variant`; the ΔF field names `lemma`, `gloss`, `example`,
  `regional_variant` and `register_variant` resolve to themselves. So a council word exposes `lemma`, `gloss`,
  `example`, `regional_variant` and `register_variant` chips. Rows with `kind` `previous_*` (undo values, below)
  are not listed.
- `support` is `"<support>/<council_runs.models_requested>"` for a word with a `council_run_id` (for example
  `"2/4"`: the alternatives that value had behind it, over the models the run requested). A legacy row has no run,
  so `support` is omitted. Council `regional_note` alternatives are stored and snapshotted but are deliberately not
  chips, on the read and the write path (see "Council chips and the example round trip").
- `lastFlag` is the newest `needs_change` verdict for the word, any version.
- `version` is `words.version`. Every action must echo it as `wordVersion`.

### `POST /api/review/actions`

One request is one SQLite write transaction (`BEGIN IMMEDIATE`), committed once at the end. A valid batch always
answers 200 with one result per action, in request order.

```ts
// request
{ actions: {
    clientReviewId: string;   // GUID; offline replay key
    wordId: number; wordVersion: number;
    action: "approve" | "approve_alt" | "approve_edit" | "reject" | "needs_change" | "demote";
    field?: string; senseOrder?: number; alternativeId?: number;
    edits?: { field: string; senseOrder?: number; after: string }[];
    note?: string; decisionMs?: number; viaBatch?: boolean;
  }[] }
// response
{ results: { clientReviewId: string; result: "applied" | "duplicate" | "stale_version" | "invalid_transition" | "invalid";
             word?: { status: string; version: number } }[] }
```

Batch-level 400 `bad_request`: no `actions`, 0 or more than 50 actions, or more than 25 items with
`viaBatch: true`. `decisionMs` is clamped to 0–600000. `clientReviewId` is echoed exactly as sent; it is stored in
lower-case `D` form, so case variants of one GUID are the same key.

#### State machine

Any (action, status) pair outside this matrix is `invalid_transition` and writes nothing
(`ReviewTransitions.StrictFrom`).

| Action | Allowed from | Effect when applied |
| --- | --- | --- |
| `approve` | `pending_review`, `rejected` | status `approved`; `approve` revision at the same version; `status_changed_at` and `approved_version` stamped; missing cards created, `word_not_approved` suspensions released; FSRS state untouched — except from `rejected` (undo of an objection, below) |
| `approve_alt` | `pending_review`, `approved` | chip value replaces the current value; version + 1; `edit` and `approve` revisions at the new version; old value kept as a `previous_<field>` alternative; then as `approve`, plus `epoch` + 1 on existing cards. An `example` chip also replaces `example_en` with the chip's `gloss` |
| `approve_edit` | `pending_review`, `approved` | 1–10 edits applied; otherwise as `approve_alt`. An `example` edit clears `example_en`/`exampleGloss` (the wire form carries no English) and keeps the old English on the undo row |
| `reject` | `pending_review`, `approved` | status `rejected`; `reject` revision; version unchanged; cards suspended (below) |
| `needs_change` | `pending_review` | status `needs_change`; `needs_change` revision; optional `field` stored on the verdict |
| `demote` | `approved` | status `pending_review`; `demote` revision; cards `epoch` + 1 and `suspended_reason` set to `word_not_approved` where empty; FSRS state, due, reps, lapses untouched; `approved_version` kept |

From `draft`, `needs_change` or `retired` every action is `invalid_transition`; from `rejected` only `approve` is
legal. The CLI (`content approve`, `content reject`) uses the same code with its own allowed-from sets
(`06-content-cli.md`).

#### Objection and its undo

- **`reject` on an approved word suspends, never deletes.** Every card of the word gets `epoch` + 1 and
  `suspended_reason = 'word_not_approved'` where no reason was set; a `manual` or `leech` pause keeps its reason.
  `review_log.card_id` is `ON DELETE NO ACTION`, so cards and learner history stay; the `reject` revision and the
  `word_reviews` verdict are appended. The word leaves `/api/learn/*` sessions because only approved words with
  unsuspended cards are offered (`07-learn-api.md`).
- A rejected word stays reachable: `GET /api/review/queue?status=rejected` (or `status=all`) lists it with `wordId`,
  `version` and `status`, which is everything `approve` needs.
- **`approve` from `rejected` restarts the suspended cards fresh at a new epoch.** Only cards whose reason is
  `word_not_approved` are reset (`ContentCardLifecycle.ResetAfterRejectionAsync`): `epoch` + 1, state New, step,
  stability, difficulty, last review, buried/introduced dates cleared, reps and lapses 0, leech flag cleared, due now,
  suspension cleared. The pre-objection FSRS schedule is not resumed. `manual` and `leech` cards are not touched.
  Missing direction cards are created. No `review_log`, `word_reviews` or `word_revisions` row is deleted; an
  `approve` revision is appended at the same version. The reset runs on the CLI path too (`content approve <key>`).
- Retrying an objection: a second `reject` with a **new** `clientReviewId` on an already rejected word is a per-item
  `invalid_transition`; a resend of an applied `clientReviewId` is `duplicate` (checked first). Neither writes
  anything, and the batch still answers 200.

#### Per-item results

Checks run in this order; the first that fails decides the result.

| Result | When | `word` |
| --- | --- | --- |
| `invalid` | the item's shape is wrong (rules below) | absent |
| `duplicate` | `clientReviewId` is already stored, or was applied earlier in the same batch | absent |
| `invalid` | `wordId` does not exist | absent |
| `stale_version` | `wordVersion` ≠ current `words.version` | absent |
| `invalid` | `viaBatch: true` on a word that is not `band = "alta"` with no lint warnings | absent |
| `invalid_transition` | current status does not allow the action (matrix above) | absent |
| `invalid` | content rule fails (rules below) | absent |
| `applied` | written | new `status` and `version` |

`invalid` and `invalid_transition` are both final for that payload: `invalid` means the request item is wrong and
must not be resent unchanged; `invalid_transition` means the item is well-formed but the word's status has moved
(for example, a second device already approved it). Only `applied` items record their `clientReviewId`; a retry of
any other result is evaluated again.

`stale_version` does not return the current version. The client must re-fetch the queue or the approved list to get
it, then decide again. The approved list carries `version` for exactly this reason (`bb1a015`): a word approved
through `approve_edit`/`approve_alt` is at version ≥ 2, and `demote` from the list must echo that version.

Shape rules (`invalid`): `clientReviewId` not a GUID; `wordId` or `wordVersion` ≤ 0; unknown `action`; negative
`senseOrder`; `field` outside `lemma`, `gloss`, `example`, `regional_note`, `regional_variant`, `register_variant`,
`article`, `gender`, `accepted`, `whole`; `alternativeId` present on anything but `approve_alt`, or missing / ≤ 0 on
it; `edits` on anything but `approve_edit`; `approve_edit` with `field` or `alternativeId`, or with 0 or more than 10
edits; an edit with a null `after`, a negative `senseOrder`, or a field other than `lemma`, `gloss`, `example`,
`regional_note`, `article`, `gender`, `accepted`; `field` on `approve`, `reject` or `demote`; `viaBatch: true` on
anything but `approve`.

Content rules (`invalid`):

- `approve_alt`: the alternative must belong to the word and resolve to a field through `field ?? kind` (above); a
  `field` in the request must equal that field. `lemma`, `regional_variant` and `register_variant` chips replace the
  headword. `gloss`/`example` chips (council rows, and the `previous_gloss`/`previous_example` undo rows) need a
  `senseOrder` of an active sense. A chip whose field resolves to `regional_note` is `invalid`: it is never offered.
  A chip equal to the current value is `invalid`.
- `approve_edit`: `article` ∈ `el`, `la`, `los`, `las`; `gender` ∈ `masculine`, `feminine`, `common`,
  `not_applicable`; `gloss`, `example` and `accepted` need the `senseOrder` of an active sense; `accepted` is a
  comma-separated list that must not be empty after trimming; an edit whose `after` equals the current value is
  `invalid`. The whole edit list is validated before anything is changed.

#### Verdict row

Every `applied` item inserts one `word_reviews` row in the same transaction: `word_version` = the request's
`wordVersion`; `field` = the edited field (`whole` for more than one edit), the chip's field, or the `needs_change`
field; `chosen_alternative_id`; `edit_diff` = JSON array of `{field, sense, before, after}` for `approve_edit` /
`approve_alt`; `note`; `client_review_id` (unique); `via_batch` 0/1; `agreement_at_review` = `words.council_agreement`
at decision time; `decision_ms`; `created_at`. `council_run_id` stays NULL: M3b added the column, but no writer fills
it (the queue reads `words.council_run_id` only, to compute chip `support`). CLI approvals and rejections, and the
approval an import grants on arrival, write no verdict row (`reviewer_id` is NOT NULL); their audit record is the
`word_revisions` row.

### `GET /api/review/approved`

```ts
{ items: { wordId: number; key: string; version: number; headword: string; article?: string; gloss: string;
           topic?: string; approvedAt: string }[] }
```

- `version` is the current `words.version`; echo it as `wordVersion` when sending `demote`.
- `gloss` is the first active sense's gloss. `q` (trimmed) matches `headword`, `key` or `gloss`, case-insensitive.
- `approvedAt`: `status_changed_at`, else the newest `approve` revision, else `words.updated_at`, else
  `created_at`. Words approved before M4 have no `status_changed_at`.
- Order: `approvedAt` descending, then `wordId` descending. `limit` default 20, maximum 50.
- Paging: there is no `next` field. To get the next page, send `after` = the `wordId` of the last item received,
  with the same `q`. A page shorter than `limit` is the last one.
- **Stale cursor.** `after` must name a word that is still in the approved list for that `q`. If it was demoted or
  rejected (or
  changed so it no longer matches `q`) between pages, the server answers 400 `bad_request`. The client must restart
  at page 1 (no `after`). `after` that is not a positive integer is also 400.

## Council chips and the example round trip (M3b)

Shipped with `CouncilImport` and the `import-council` verb (`06-content-cli.md`). These are the precedents the C3/C4
reviews asked to have written down, not incidental details:

- **Resolution is `field ?? kind`.** `ReviewTransitions.ChipFieldFor(field, kind)` reads `word_alternatives.field`
  first and falls back to `FieldForAlternativeKind(kind)`; the same resolver serves the queue projection and
  `approve_alt`, so the read and the write path cannot disagree about what a row is. Council rows store the ΔF field
  name in both columns (`kind = field`), so they resolve either way.
- **`kind = field` rows are visible as chips.** `lemma`, `gloss`, `example`, `regional_variant` and
  `register_variant` all resolve to themselves. `approve_alt` on a `gloss` chip swaps `senses.en_gloss`; on an
  `example` chip it swaps `senses.example_es`; on the three variant/lemma fields it swaps the headword.
- **`regional_note` is stored, never a chip.** Council bundles may carry `regional_note` alternatives; the importer
  writes them to `word_alternatives` (`field='regional_note'`) and into the revision snapshot, so nothing is lost,
  but neither read nor write path offers them: the sheet has no `regional_note` tab and `approve_alt` has no swap
  target for a note (it would fall through to `example_es` and corrupt the example). This is a recorded UI decision,
  not an omission; revisiting it needs a UI design, not a bug fix.
- **An `example` chip swap refreshes the English too.** The chip's `gloss` is the bundle's English translation of the
  alternative sentence, so `approve_alt` writes it to `senses.example_en` and to the snapshot's `exampleGloss` when
  it is non-empty. A legacy `previous_example` undo row carries no gloss, so its behaviour is unchanged.
- **An `example` edit clears the English.** `approve_edit` carries only the new Spanish sentence (no English input
  exists on the wire), so it sets `example_en = ""` and `exampleGloss = ""` instead of leaving the translation of the
  sentence it replaced. The old English is not lost: it goes on the undo row (next bullet). Clearing changes what
  the learner card shows until the reviewer undoes or a rework supplies a translation; the alternatives were keeping
  stale English (wrong invariant) or refusing example edits (blocks typo fixes), and this is the honest one.
- **The undo round trip restores both languages.** Every `approve_edit`/`approve_alt` keeps the replaced value as a
  `previous_<field>` alternative. For an example, that row's `word_alternatives.gloss` now holds the replaced
  English sentence; undo is another `approve_alt`, which already consumes `gloss` as the new `example_en`. So
  swap → undo restores the Spanish and the English. No column was added for this.

The chip `support` numerator is `word_alternatives.support`; the denominator is
`council_runs.models_requested` for the word's run, loaded once per queue page. A word whose run row is missing
(should not happen; deletions are blocked by the FK) omits `support` rather than showing `"n/0"`.

## Export and join semantics

No export endpoint reads `word_reviews` yet (plan step 5). A future export must join it as follows:

- `approve`, `reject`, `needs_change`, `demote` do not change the version. The decision's revision is the
  `word_revisions` row with the same `word_id`, `version = word_reviews.word_version`, the same action name and the
  same `created_at` (one timestamp per request; a batch shares it).
- `approve_edit` and `approve_alt`: `word_reviews.word_version` is the **pre-edit** version N (`ReviewEndpoints.cs:196`).
  The `edit` and `approve` revisions are written at N + 1, and `words.approved_version` is set to N + 1
  (`ReviewTransitions.cs:161`, `:407`). So `word_revisions` at `version = word_version` is the content the reviewer
  saw; the approved content is `version = word_version + 1`, `action = 'approve'`. `edit_diff` is the direct record of
  what changed.
- Do not join `word_reviews.word_version` to `words.approved_version`: they differ by one for edit approvals,
  `approved_version` is NULL for words approved before M4, and it keeps the last approved version after a demote or a
  changed-source re-import.
- `chosen_alternative_id` becomes NULL when a re-import replaces the word's alternatives (FK `SET NULL`); the verdict
  row and `edit_diff` survive. `previous_*` undo alternatives are also replaced by a re-import.
- CLI approvals and rejections appear only as `word_revisions` rows with `actor_user_id` NULL, never in
  `word_reviews`. An import-time approval appears only as the `import` revision (snapshot `status: "approved"`).

## Re-import after a reviewer decision (Q-M4-3)

Shipped in `89657e4` (run `T-20261019-2013`, plan step 4; 15 cases then), amended for trust-by-default in `7234a15`
and `37e1371`. Tests: `M4ReviewReimportTests.cs`. CLI behaviour
and the hash definition: `06-content-cli.md`. This section covers only how a re-import interacts with verdicts and
versions.

- `content_hash` is the hash of the last imported **source** file. `approve_edit` and `approve_alt` never write it.
- **Source unchanged since the last import** → the import is `unchanged` and performs no write at all: the reviewer's
  content, `approved` status, `version`, hash, verdict row, alternatives and card epochs all survive. Both orderings
  are covered (reviewer action → import, and repeated imports after it).
- **Source changed** → the import wins the content: content replaced, `version` + 1. The resulting status depends on
  the word (`ContentImporter`, `37e1371`):
  - `approved` and **no** reviewer `edit` revision → stays `approved`, `approved_version` = the new version, cards
    `epoch` + 1 but not suspended. Trust-by-default: a source refresh never silently drops an accepted word from the
    learner's sessions.
  - `approved` **with** at least one `edit` revision (written by `approve_edit` and `approve_alt`) → `pending_review`,
    cards demoted (`epoch` + 1, `suspended_reason` set to `word_not_approved` where it was empty), so a reviewer edit
    is never overwritten unseen.
  - any other status (`pending_review`, `rejected`, `needs_change`, `draft`) → `pending_review` and cards demoted, as
    before. A rejected word with changed source returns to the pending scope.
- In the demoting cases the reviewer's `edit`/`approve` snapshots stay at their old versions, her `word_reviews` row
  still names the version she saw, and `approved_version` stays the last approved version — an audit fact, not
  approval of the new version. No revision is deleted or renumbered, so no verdict points at a vanished version. A
  queued action for the old version returns `stale_version` in every case; the queue exposes the new version.
- **The edit marker is permanent.** The check is "an `edit` revision exists for this word", not "an edit not yet
  superseded". After a demote, if the reviewer accepts the new source with a plain `approve`, the old `edit` revision
  remains and every later changed-source import demotes the word again. Conservative by choice
  (`.opencode/runs/T-20261019-2100/37-queue-default-review.md` finding 3).
- Replaying the original action after the import returns `duplicate`, not `stale_version`: the duplicate check runs
  first.
- A changed-source import replaces the word's alternatives, so `chosen_alternative_id` becomes NULL (`ON DELETE SET
  NULL`) while `edit_diff` and the snapshots keep the chosen value (see "Export and join semantics").
- An unchanged re-import is idempotent; a second identical import after a changed one changes nothing further.
- Import takes `BEGIN IMMEDIATE` before its first read, like `POST /api/review/actions`, so the two serialise.

Reviewer edits are validated against the import schema's field constraints before any tracked entity changes
(`Content/ReviewContentValidation.cs`): lengths, `article`/`gender` enums, accepted-answer lists, the noun/phrase gender
condition, and the 60-character headword limit even when the alternative's own limit is 80. Lexical/style lint is still
deferred (Q-M4-4): no lexicon matcher runs on edits, so an edit can pass every server check and still be a
Peninsular/register regression, and `lintWarnings` is not recomputed.

## Known limitations

- A pending word without a current-version snapshot (corpus corruption) throws: the whole batch rolls back and the
  request answers 500. An offline queue that resends the same batch fails the same way.
- `viaBatch: true` on a word that is no longer pending returns `invalid`, not `invalid_transition` (the batch
  eligibility check runs first).
- `stale_version` omits the current version (see above).
- The GET routes read without a transaction; a concurrent write can show a just-decided word once. Acting on it
  returns `invalid_transition` or `stale_version`.
- Queue and approved list load every matching word and page in memory; fine for a corpus of hundreds of words.
- Concurrency safety rests on SQLite's write lock (`BEGIN IMMEDIATE`); no test sends concurrent requests. Import uses
  the same lock (see the section above), and the orderings are tested serially in both directions.
- An `approve_edit` batch that sets both `article` and `gender` on a noun can be rejected even though the result is
  valid: each edit is validated against the pre-edit article. Editing the article first works. Rejects valid input
  only.
- Reordering the `topics` array in the source file changes the import hash though the meaning is unchanged; the next
  import bumps the version and epoch-bumps the cards. An approved word without a reviewer edit stays approved; one
  with an edit, or any non-approved word, returns to `pending_review` with suspended cards. A reviewer edit is never
  lost.
- Import-then-edit at the new version is not asserted end to end: after a changed-source import the tests check
  `stale_version` for the old version and the queue's new version, not a fresh `approve_edit` at that version (same
  transition code, already covered elsewhere).
- `content approve` (CLI) still opens a deferred transaction (`Content/ContentApproval.cs:17`); racing an import can
  fail with `SQLITE_BUSY_SNAPSHOT` / "database is locked". No corruption, rerunnable; align to `IMMEDIATE` later.
- The FK-clean and dangling-verdict assertions for the changed-source path are in the probe
  (`.opencode/runs/T-20261019-2013/00-reimport-probe.mjs:85,88`), not in the unit tests.
- `lintWarnings` is not recomputed for reviewer edits (Q-M4-4). Edits do face the import schema's field constraints
  (`Content/ReviewContentValidation.cs`); lexical/style lint does not run.
- `regional_variant`/`register_variant` chips replacing the headword is an implementation choice not defined in the
  plan; confirm with the reviewer UI design.
