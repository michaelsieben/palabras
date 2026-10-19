# Content difficulty ranks

Dependency-free authoring tool. The reviewable list is
`content/frequency/peninsular-a1a2.txt` (includes B1 despite its historical name).
The importer reads **only the generated `difficulty` property in each word JSON**;
the list is not an importer sidecar.

```bash
node tools/content-rank/index.js --check          # default; fails on drift
node tools/content-rank/index.js --check --preview # first/last ten per CEFR
node tools/content-rank/index.js --write          # regenerate before importing
node --test tools/content-rank/index.test.js
node tools/content-lint/index.js
```

Each nonblank, noncomment line gives its exact headword rank `10 × position`.
Ordering uses CEFR first, rank second. Comments explain acquisition priorities;
these are qualitative authoring judgements, not measured frequency estimates.
Every corpus headword must appear exactly once; missing, extra and duplicate
headwords fail before any write. New batches must extend the list. The schema
allows unranked future drafts; this tool requires ranks for shipped files.
Ranks are unique globally (therefore within each CEFR), integer multiples of ten,
with maximum 9999. Blank lines/comments do not reserve numeric gaps.

Writes insert one line after `cefr`, preserving all other source bytes and line
endings. Existing rank lines are normalized; a second write changes nothing.
Inserting or moving headwords re-ranks subsequent entries. Ranks participate in
`content_hash`: re-importing a changed rank demotes approved words under existing
import rules. Finalize ranks **before** first import and approval.

Run `--check` alongside content-lint in the pre-import/CI gate. Check verifies the
mapping and formatting; lint validates the optional schema field and vocabulary.
Native review remains required: neither command proves acquisition difficulty.
