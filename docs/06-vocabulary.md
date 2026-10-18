# Vocabulary (M2)

`words` has integer `id` PK and unique `external_id` from `content/schema/word.schema.json`.
Headword is never a key. `senses` and `alternatives` have integer PKs, `word_id` FKs and unique
`(word_id, position)` slots; unchanged slots retain ids during re-import. The schema attaches
alternatives to the **word**, not a guessed sense. `kind` preserves the exact schema enum:
`synonym`, `near-synonym`, `different-sense`, `regional`, `register`. `sense.notes` stores regional
qualifications. Alternatives are imported, never generated; no LLM dependency.

`word_topics` has PK `(word_id, topic)` and reverse index `(topic, word_id)` for exact tag filters.
Separate indexes on `words.status`, `cefr` and `part_of_speech` serve review queues, level and grammar
filters. Child slot indexes serve word-detail reads. SQL CHECKs enforce lifecycle and alternative kind.
Source timestamps populate `created_at` and `updated_at` (`createdAt` if `updatedAt` absent).
Canonical `content_json` retains every schema field, including provenance/review/lint metadata.

**Learner selection must use `PalabrasDbContext.WordsForSession()`**, defined in
`Persistence/VocabularyModel.cs`: SQL filters `status = 'approved'`. Authenticated catalog reads
are not sessions and may inspect other states. All 58 seeds import as **pending_review**;
import does not auto-approve. Import refuses an existing `draft`/`rejected` → `approved` promotion;
a future reviewer workflow must own that transition. Rejection retains the word row, id,
senses and alternatives. No SRS fields exist; add separate future state tables referencing
`words.id`, `senses.id` and `users.id`, never headword/username.

## Import

```sh
dotnet run --project Backend/Palabras -- import content/words
docker exec -it palabras /app/Palabras import /content/words
```

Server form requires content already mounted/copied at `/content`; image does not bundle seeds.
Directory import reads immediate `*.json` children; filenames must equal `<id>.json`.
Schema validation rejects unknown/duplicate fields, wrong types/enums, invalid dates, conditional
noun/phrase violations and anything outside 2–4 alternatives. Whole batch validates before writes;
transaction rolls back all upserts on any error. Per-file created/updated/skipped/failed output,
summary, exit 0 success / 1 failure. Re-import of equivalent JSON skips without changing timestamps
or ids. Missing entries are not deleted. Explicitly removing child array slots removes those slots;
reordering changes their content, so future references must treat positions as source slots.

Runtime embeds an exact backend copy of the authoritative schema because Docker builds only
Backend. When changing schema, update `Backend/Palabras/Vocabulary/word.schema.json` too;
`EmbeddedSchemaIsExactlyAuthoritativeSchema` prevents drift. Validator supports only schema's
current keyword subset; extend validation tests when adding keywords.

## Read / inspect

Cookie-authenticated `GET /api/words?status=pending_review&level=A1&topic=food&page=1&pageSize=20`
returns `{items,total,page,pageSize}`; optional `partOfSpeech` filter. Page size 1–100; ordering by
external id. `GET /api/words/es-coger-01` returns schema-shaped content including senses and
alternatives; missing id 404, anonymous request 401. Existing API 404 and authenticated SPA fallback
remain intact. No exercise/session/reviewer mutation endpoints added.

Inspect a local backup or throwaway SQLite file with `sqlite3` (not installed in app image):

```sh
sqlite3 /path/to/palabras.db 'SELECT status, COUNT(*) FROM words GROUP BY status;'
sqlite3 /path/to/palabras.db 'SELECT COUNT(*) FROM senses; SELECT COUNT(*) FROM alternatives;'
sqlite3 /path/to/palabras.db 'SELECT external_id, headword, status FROM words ORDER BY external_id LIMIT 10;'
```

Use SQLite's backup mechanism for live databases; do not copy a lone WAL-mode DB file.
