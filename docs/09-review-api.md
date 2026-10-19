# Reviewer API (M4)

Shipped in T-20261019-1606: the reviewer gate over `word_reviews` (`3565e75` schema, `e75b850` endpoints,
`bb1a015` approved-list `version`). Code: `Backend/Palabras/Review/ReviewEndpoints.cs` (routes),
`ReviewContracts.cs` (wire shapes), `ReviewTransitions.cs` (state machine shared with the CLI). The plan is
`.opencode/runs/T-20261019-1508/10-m4-plan.md` §2; this file describes the shipped code. The reviewer UI (`/revisar`,
`/aprobadas`) is not covered here. CLI approval: `06-content-cli.md`. Learner API conventions: `07-learn-api.md`.
Migration and rollback: `08-upgrade-rollback.md` section G.

## Common rules

- Every route requires policy `reviewer` (role `reviewer`). The owner account has no reviewer role, so the owner →
  403 `{"error":"forbidden"}`; anonymous → 401 `{"error":"unauthorized"}`. The reviewer gets 403 on `/api/learn/*`
  (`07-learn-api.md`).
- `POST /api/review/actions` needs `X-Palabras: 1`, else 400 `{"error":"missing_x_palabras"}`. The two GET routes do
  not. A body that does not bind → 400 `{"error":"bad_request"}`.
- The reviewer is always the cookie's user id; no body field names a reviewer.
- JSON is camelCase. Optional members are omitted, not sent as `null`. Instants are ISO 8601 UTC.

## Routes

| Method + route | Success | Errors |
| --- | --- | --- |
| `GET /api/review/queue?limit=` | 200 queue page | 400 `bad_request` (`limit` not an integer in 1–30) |
| `POST /api/review/actions` | 200 per-item results | 400 `bad_request` (batch shape), 500 (corrupt word, see limitations) |
| `GET /api/review/approved?q=&after=&limit=` | 200 page | 400 `bad_request` (`limit` not 1–50, bad or stale `after`) |

All rows also return the 401/403 above.

### `GET /api/review/queue`

Returns the first `limit` (default 30, maximum 30) `pending_review` words. There is no cursor: after acting, fetch
again.

```ts
{ items: ReviewItem[]; pendingTotal: number; topics: { topic: string; pending: number }[] }

ReviewItem = {
  wordId: number; key: string; version: number; headword: string; article?: string; gender?: string;
  pos: string; cefr: string; topic?: string;
  senses: { order: number; gloss: string; acceptedEn: string[]; acceptedEs: string[];
            exampleEs: string; exampleEn: string; notes?: string }[];
  notes?: string; regionNotes?: string; registerNotes?: string;
  agreement?: number; band?: string; reducedConfidence: boolean; lintWarnings: string[];
  alternatives: { id: number; field: string; text: string; translation?: string; reason: string; support?: string }[];
  lastFlag?: { action: "needs_change"; note?: string; at: string };
}
```

- `pendingTotal` counts every pending word, not only the page. `topics` lists pending counts per topic, largest first,
  then by name; words without a topic are not listed there.
- Order: the topic with the most pending words first; inside a topic `agreement` descending with missing values last;
  then `key` (ordinal). A word's topic is `topics[0]` of its current-version snapshot, else the alphabetically first
  `word_topics` row.
- `senses` are the active senses of the current version (`sense_order` below the snapshot's sense count).
- `alternatives` maps the stored `word_alternatives.kind` to the reviewer field: `synonym`, `near-synonym`,
  `different-sense` → `lemma`; `regional` → `regional_variant`; `register` → `register_variant`. Rows with
  `kind` `previous_*` (undo values, below) are not listed. `support` is never set in M4.
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

Each action is legal from exactly one status. Any other pair is `invalid_transition` and writes nothing.

| Action | Allowed from | Effect when applied |
| --- | --- | --- |
| `approve` | `pending_review` | status `approved`; `approve` revision at the same version; `status_changed_at` and `approved_version` stamped; missing cards created, `word_not_approved` suspensions released; FSRS state untouched |
| `approve_alt` | `pending_review` | chip value replaces the current value; version + 1; `edit` and `approve` revisions at the new version; old value kept as a `previous_<field>` alternative; then as `approve`, plus `epoch` + 1 on existing cards |
| `approve_edit` | `pending_review` | 1–10 edits applied; otherwise as `approve_alt` |
| `reject` | `pending_review` | status `rejected`; `reject` revision; version unchanged |
| `needs_change` | `pending_review` | status `needs_change`; `needs_change` revision; optional `field` stored on the verdict |
| `demote` | `approved` | status `pending_review`; `demote` revision; cards `epoch` + 1 and `suspended_reason` set to `word_not_approved` where empty; FSRS state, due, reps, lapses untouched; `approved_version` kept |

From `draft`, `needs_change`, `rejected` or `retired` every action is `invalid_transition`; from `approved` every
action except `demote` is. The CLI (`content approve`) uses the same code with an "any status" rule
(`06-content-cli.md`).

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

- `approve_alt`: the alternative must belong to the word and map to a field; a `field` in the request must equal
  that field. `lemma`, `regional_variant` and `register_variant` chips replace the headword. `gloss`/`example` chips
  (only `previous_gloss`/`previous_example` undo rows) need a `senseOrder` of an active sense. A chip equal to the
  current value is `invalid`.
- `approve_edit`: `article` ∈ `el`, `la`, `los`, `las`; `gender` ∈ `masculine`, `feminine`, `common`,
  `not_applicable`; `gloss`, `example` and `accepted` need the `senseOrder` of an active sense; `accepted` is a
  comma-separated list that must not be empty after trimming; an edit whose `after` equals the current value is
  `invalid`. The whole edit list is validated before anything is changed.

#### Verdict row

Every `applied` item inserts one `word_reviews` row in the same transaction: `word_version` = the request's
`wordVersion`; `field` = the edited field (`whole` for more than one edit), the chip's field, or the `needs_change`
field; `chosen_alternative_id`; `edit_diff` = JSON array of `{field, sense, before, after}` for `approve_edit` /
`approve_alt`; `note`; `client_review_id` (unique); `via_batch` 0/1; `agreement_at_review` = `words.council_agreement`
at decision time; `decision_ms`; `created_at`. `council_run_id` stays NULL until M3b. CLI approvals write no verdict
row.

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
- **Stale cursor.** `after` must name a word that is still in the approved list for that `q`. If it was demoted (or
  changed so it no longer matches `q`) between pages, the server answers 400 `bad_request`. The client must restart
  at page 1 (no `after`). `after` that is not a positive integer is also 400.

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
  `approved_version` is NULL for words approved before M4, and it keeps the last approved version after a demote.
- `chosen_alternative_id` becomes NULL when a re-import replaces the word's alternatives (FK `SET NULL`); the verdict
  row and `edit_diff` survive. `previous_*` undo alternatives are also replaced by a re-import.
- CLI approvals appear only as `word_revisions` rows with `actor_user_id` NULL, never in `word_reviews`.

## Known limitations

- A pending word without a current-version snapshot (corpus corruption) throws: the whole batch rolls back and the
  request answers 500. An offline queue that resends the same batch fails the same way.
- `viaBatch: true` on a word that is no longer pending returns `invalid`, not `invalid_transition` (the batch
  eligibility check runs first).
- `stale_version` omits the current version (see above).
- The GET routes read without a transaction; a concurrent write can show a just-decided word once. Acting on it
  returns `invalid_transition` or `stale_version`.
- Queue and approved list load every matching word and page in memory; fine for a corpus of hundreds of words.
- Concurrency safety rests on SQLite's write lock (`BEGIN IMMEDIATE`); no test sends concurrent requests.
- Not covered by tests yet (plan step 4): re-import of a file after a reviewer edit (unchanged file keeps the edit;
  changed file overwrites it, Q-M4-3), and a learner attempt queued against a demoted card.
- `lintWarnings` is not recomputed for reviewer edits (Q-M4-4).
- `regional_variant`/`register_variant` chips replacing the headword is an implementation choice not defined in the
  plan; confirm with the reviewer UI design.
