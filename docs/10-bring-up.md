# Study bring-up: first deploy with real words (owner-run)

Operator runbook for the first production deploy that carries content ranking and the `learn` CLI (run
`T-20261019-2100`, commits up to `5b818d1`/`3a0d52a`), followed by account, content and placement setup so the owner
can study real approved words on Android Chrome. Plan of record: `.opencode/runs/T-20261019-2100/01-tomorrow-plan.md`
§3–§7.

Verification status of this file: the CLI command lines and their output shapes were executed against a Release build
of `3a0d52a` on throwaway databases (`.opencode/runs/T-20261019-2100/12-cli-shape-verify.md`, 48 commands); every
command line below parses and runs. **Nothing here has been run against production by the docs step**, and host-only
facts (poll state, `deploy.log`, `$DEPLOY/backups`) were not readable from the repo container
(`03-prod-preflight.md` §4).

Related docs, not repeated here: accounts `04-accounts.md`; content CLI `06-content-cli.md`; learner API
`07-learn-api.md`; upgrade, rollback and the `ReviewGate` rebuild `08-upgrade-rollback.md` (§A, §F, §G); reviewer API
`09-review-api.md`; poll settings `../README.md` ("Automatic updates, pause and rollback").

```bash
DEPLOY=/mnt/user/docker/palabras        # compose folder, poll state files, poll backups
REPO=/mnt/user/repos/palabras           # host checkout the poll builds
```

Account names below are placeholders. At the 2026-10-19 preflight production held exactly two accounts: owner
`michael` and reviewer `ana` (`03-prod-preflight.md` §3).

## Trigger

Local `main` carries three migrations production has not applied, plus the ranked corpus and the `learn` /
`content approve --top` verbs, and the owner wants to start studying.

## 1. Preconditions

All must hold before `git push`. Stop at the first one that fails.

1. **Pre-push gate is green locally.** The poll consults no CI (`08-upgrade-rollback.md` §E.3 item 4), so this is the
   only gate:

   ```bash
   dotnet build Backend/Palabras.slnx
   dotnet test Backend/Palabras.slnx
   npm --prefix Frontend ci && npm --prefix Frontend run lint && npm --prefix Frontend run build
   node tools/content-lint/index.js            # prints the word-file count used below
   node tools/content-rank/index.js --check    # every word file carries a rank
   docker build -t palabras:local .            # image builds
   ```

2. **Poll is active and not paused** (host):

   ```bash
   if ls -d "$DEPLOY/paused" >/dev/null 2>&1; then
     echo 'PAUSED - remove only if you intend the poll to deploy'
   else
     echo 'poll not paused (expected)'
   fi
   tail -n 20 "$DEPLOY/deploy.log"             # recent five-minute ticks, no repeated refusal
   cat "$DEPLOY/deployed.sha"                  # expect 0c5afc3...
   ```

   The probe is inverted on purpose: `ls "$DEPLOY/paused" && echo …` exits `1` when the poll is **not** paused,
   which is the good state, and the "stop at the first failure" rule above would halt a correct run.

   Also confirm in User Scripts that `palabras-auto-deploy` is scheduled (`*/5 * * * *`). At the preflight the poll
   could not be shown to run: `origin/main` was ~22 h ahead of production (`03-prod-preflight.md` §4).

3. **Production is still empty of content** (decides the deploy path):

   ```bash
   docker exec palabras /app/Palabras content status    # expect total=0
   docker exec palabras /app/Palabras user list         # expect the owner and reviewer rows
   curl -s http://127.0.0.1:18480/healthz               # expect "commitShort":"0c5afc3"
   ```

   - `total=0`: `ReviewGate` rebuilds empty `words`/`word_revisions`; the poll path below is acceptable.
   - `total>0`: **do not use the poll.** Pause it and follow `08-upgrade-rollback.md` §A + §E.4 + §G.5.

4. **Backups exist, plus a stopped snapshot for this deploy.** The app's nightly copy sits in `/data/backups/` (no
   `keys/`); the poll takes its own copy (database, `-wal`, `-shm`, `keys/`) into
   `$DEPLOY/backups/<UTC stamp>-<sha7>/` before rebuilding and aborts the deploy if the copy fails. Those are hot
   copies taken while the app writes; treat them as secondary evidence. Because this deploy uses the poll exception
   below, take a **stopped** set as well, outside the poll's five-backup rotation, before `git push`:

   ```bash
   touch "$DEPLOY/paused"
   cd "$DEPLOY" && docker compose down --timeout 60
   SAFE="$DEPLOY/bringup-$(date -u +%Y%m%dT%H%M%SZ)"
   mkdir -m 700 "$SAFE"
   cp -a /mnt/cache/appdata/palabras/palabras.db* "$SAFE/"   # db + -wal + -shm when present
   cp -a /mnt/cache/appdata/palabras/keys "$SAFE/keys"       # fail closed if missing
   docker tag "$(docker inspect palabras --format '{{.Image}}')" palabras:local   # restart the old image
   docker compose up -d --no-build --force-recreate          # old image is still palabras:local here
   ```

   No Palabras or CLI process may touch the database during the copy. With real content, §G.5's stopped-app set is
   the minimum; a hot copy is not.

   ```bash
   docker exec palabras sh -lc 'ls -la /data/backups'
   ```

5. **Retain the outgoing image before replacement** (host). Tag it while the old container is still running; a tag
   created after the poll has replaced the container cannot recover the outgoing image:

   ```bash
   OLD_ID=$(docker inspect palabras --format '{{.Image}}')
   docker tag "$OLD_ID" "palabras:pre-$(date -u +%Y%m%dT%H%M%SZ)"
   docker image inspect "$OLD_ID" --format '{{.Id}}'   # note ID and tag; keep both until acceptance
   ```

   The poll additionally preserves the same ID as `palabras:rollback-<image-id12>` (for the current image,
   `palabras:rollback-9abfd00daeb2`), newest two kept. It names the tag from the image ID, not the commit SHA.

### Policy exception: poll instead of §G's manual paused upgrade

`08-upgrade-rollback.md` §G requires a **manual, paused** upgrade. The owner has chosen the automatic poll for this
one deploy because the database holds zero words: on an empty corpus the `ReviewGate` table rebuild has no rows to
copy, which is the migration's main risk. The exception is narrow and has prerequisites:

- a stopped-file snapshot (database, `-wal`/`-shm` and `keys/` copied with the container down), outside the poll's
  five-backup rotation;
- the three-migration drill on a copy (`08-upgrade-rollback.md` §A.7), including all-eight-migration acceptance
  (§2);
- `touch "$DEPLOY/paused"` immediately on any failure or repeated `deploy FAILED, rolled back` (the poll retries
  every 5 minutes and leaves the database migrated);
- the first deploy is attended, and the owner watches it to `DEPLOY OK` plus a real Android login.

Rollback is **R-B** (restore the stopped database set plus `keys/` and the retained old image), not the poll's
automatic image rollback: that restores only the image and can report old health against a partially migrated
database. If any prerequisite cannot be shown, use the manual §A path instead.

## 2. Deploy

```bash
git push origin main
```

What the poll does within ~5 minutes (`08-upgrade-rollback.md` §F.1): fetch and fast-forward the host checkout; tag
the running image as `palabras:rollback-<image-id12>` (newest two kept, listed in `$DEPLOY/rollback-images`); copy the
data set to `$DEPLOY/backups/`; `docker compose up -d --build` with `COMMIT_SHA` set; wait up to 180 s for
`/healthz` to return `"status":"ok"`; run `user list`; write `deployed.sha`/`deployed.image` and log `DEPLOY OK`.
On a failed gate it restores the previous image, logs `deploy FAILED, rolled back` and leaves the database migrated.

The new container applies three migrations on start, in this order:

| Migration | Kind | Note |
| --- | --- | --- |
| `20261019140735_ReviewGate` | table rebuild of `words` and `word_revisions` | not one transaction; crash window in `08-upgrade-rollback.md` §G.2–G.3. Zero rows to copy on an empty DB |
| `20261019182611_CouncilImport` | additive | |
| `20261019192721_WordDifficulty` | additive: one `ALTER TABLE words ADD difficulty INTEGER NULL` | |

Acceptance covers **eight** migration IDs in `__EFMigrationsHistory`, not the three applied tonight: `InitialAccounts`,
`ContentSchema`, `SrsCards`, `DayStatusAndSkips`, `RolloverCutoverMarker`, `ReviewGate`, `CouncilImport`,
`WordDifficulty`. Do not stop or recreate the container while it is migrating (section B of
`08-upgrade-rollback.md`).

### Verification

Run in this order; continue only when each passes.

```bash
tail -n 30 "$DEPLOY/deploy.log"                              # health ok (post-deploy ...) then DEPLOY OK sha=<pushed sha>
docker inspect palabras --format '{{.State.Health.Status}}'  # healthy
curl -s http://127.0.0.1:18480/healthz                       # "commit" == git rev-parse HEAD of the push
docker image ls 'palabras:pre-*'                             # outgoing image retained before replacement (§1.5)
docker image ls 'palabras:rollback-*'                        # poll's own copy of the same ID; write it down
cat "$DEPLOY/rollback-images"
ls -d "$DEPLOY"/backups/*/ | tail -n 1                       # poll hot copy; write it down
ls -d "$DEPLOY"/bringup-*/ | tail -n 1                       # stopped set from §1.4; the R-B source
docker exec palabras /app/Palabras user list                 # same accounts as before the deploy
docker exec palabras /app/Palabras content status            # still total=0
sqlite3 /mnt/cache/appdata/palabras/palabras.db ".backup '/tmp/bringup-check.db'"
sqlite3 /tmp/bringup-check.db \
  'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1; SELECT name FROM sqlite_master WHERE name GLOB "ef_temp_*";'
# expect exactly the eight IDs listed above and no ef_temp_* rows
```

The `.backup` reads a consistent copy; the live file is not opened in place.

If the log shows `deploy FAILED, rolled back` or `rollback FAILED`, stop and go to §7 / §8.

## 3. Account setup

Two accounts, two roles. The owner account also carries the `learner` role and gets cards; the reviewer account gets
no cards and is refused on `/api/learn/*`. No account can hold both roles, and exactly one account per role is
allowed (`04-accounts.md`), so the reviewer is a separate login created via the CLI.

```bash
docker exec palabras /app/Palabras user list
```

Create only what is missing (`-it`: the password is prompted twice, hidden):

```bash
docker exec -it palabras /app/Palabras user create owner --username <owner>
docker exec -it palabras /app/Palabras user create reviewer --username <reviewer>
```

Passwords must be **12–1024 characters** (`user create` and `user set-password` reject anything shorter with
`Error: Password must contain 12-1024 characters.`). `--password-stdin` exists for non-interactive use.

**Secret hygiene, now.** The host `.env` still carries the plaintext `OWNER_INITIAL_PASSWORD` and
`REVIEWER_INITIAL_PASSWORD` variables, and the compose file passes the whole `.env` as container environment
(`deploy/unraid/docker-compose.yml:26`). The env-seeding path was rejected (`03-auth-bootstrap.md`), so the app does
not need them: remove them from the host `.env` after the accounts exist, and do not print or paste their values
anywhere. The container keeps them until it is recreated; if they were exposed beyond the Docker administrators,
rotate the account passwords with `user set-password` and recreate the container deliberately.

**The owner must exist before any approval.** Cards are created only for learner accounts that exist at approval time;
there is no automatic backfill (§7, "No cards appear").

## 4. Content load

The image ships only `content/schema/` (`Dockerfile:18`), so the word files are copied in from the host checkout.

```bash
docker cp "$REPO/content/words" palabras:/tmp/words
docker exec palabras /app/Palabras import /tmp/words --dry-run   # Dry run: created=<file count> ... rejected=0
docker exec palabras /app/Palabras import /tmp/words             # created=<file count> updated=0 unchanged=0 rejected=0
docker exec palabras /app/Palabras content status                # pending_review=<file count>
```

`<file count>` is what `node tools/content-lint/index.js` printed (527 on the throwaway run). `/tmp/words` is lost when
the container is recreated; if it already exists, `docker cp` nests the directory, so copy to a new path instead.

Import **before** approval: the rank lives in the imported `words.difficulty`. Do not re-import after approval unless
the files changed: a changed file returns that word to `pending_review` and suspends its cards
(`06-content-cli.md`, "import").

### Approve the first ranked words

**Pinned-key approval is the primary path:** dry-run the selection once, save the printed keys, have them reviewed,
then approve **those keys**. `content approve --top N` selects a fresh dynamic batch on every run, so it is a
one-shot at best; the saved key list is what you keep, audit and replay.

```bash
docker exec palabras /app/Palabras content status                                  # total=527 pending_review=527 approved=0
docker exec palabras /app/Palabras content approve --top 150 --dry-run > approval-preview.txt
# 150 keys in rank order, then dry-run=150. Review this exact list BEFORE the next command.
grep '^es-' approval-preview.txt > approved-keys.txt
test "$(wc -l < approved-keys.txt)" -eq 150
mapfile -t KEYS < approved-keys.txt
docker exec palabras /app/Palabras content approve "${KEYS[@]}"                  # approved=150 unchanged=0
docker exec palabras /app/Palabras content status                               # approved=150 pending_review=377 total=527
```

The equivalent one-shot write is `content approve --top 150` (output `approved=150 unchanged=0`). It is allowed only
when it **immediately follows an approved dry-run with no other content mutation in between**, and it must never be
replayed later as a repair. `--top N` takes the N lowest `(cefr, difficulty, id)` words **whose status is
`pending_review`**, so a second run approves the **next** N pending words: a careless repeat, or a re-run "to be
sure", over-approves.

**Repair path = the saved key list.** If cards are missing (owner account created after approval) or a word must come
back, replay the saved keys: `content approve <key…>` reports `approved=0 unchanged=N` for already-approved words and
still runs the card pass for **every named word** (verified end to end: 10 cards restored, then idempotent). The list
must be exact — one unknown key aborts the whole call (`Unknown word key(s): … No words approved.`, nothing written).
Never repair with another `--top N` selection and never with `--all-pending`.

`content approve --all-pending` is the **bulk** option: **"bulk — approves the entire remaining corpus"**. It also
repairs cards for the whole already-approved corpus, and it exceeds the starter-band policy (150 words) by design.
Use it only after a conscious owner decision to approve everything left, never as recovery.

There is no `--dry-run` for the key form and none for `--all-pending`; "always dry-run first" applies only to
`--top N`.

The reviewer gate. `content/style-guide.md` says a word enters the app only after review. CLI approval bypasses it:
it writes a `word_revisions` row with `action='approve'` and a NULL actor and **no** `word_reviews` verdict row, so
the bypass is auditable **only by the saved key list** — treat `approval-preview.txt` as the audit record and keep it
with the deploy notes (`06-content-cli.md`, "content approve"). Plan §5 accepts this for the first 150 A1 words as a
time-boxed 150-word exception; anything wider needs the owner's explicit decision.

The preferred path is that the reviewer approves in `/revisar`. Her queue is grouped **by topic, not by rank**, so it
does not present the same first 150, and she decides independently — do not assume "the reviewer approves the same
first 150". Words she approves count as approved, and a later `--top N` skips them. Do not select or approve keys
while she is reviewing.

What stays pending: 377 words (total 527 minus the 150 approved), i.e. the rest of A1, all of A2 and B1. Those words
have no cards and never reach a session until approved. Approve the next band the same way: dry-run, save, review,
approve the saved keys.

## 5. Placement for a returning learner

Goal: get past words the owner already knows without a placement screen.

```bash
docker exec palabras /app/Palabras learn settings --username <owner> --new-per-day 200
# user=<owner>
# new_per_day=12 -> 200
# max_reviews_per_day=200 -> 200
# backlog_threshold=150 -> 150
docker exec palabras /app/Palabras learn settings --username <owner>    # read-only check, no "->"
```

How the ramp works:

- New cards are offered in rank order: `cefr` first (A1 before A2 before B1), then `difficulty` (lower first), then
  word id. A word without a rank sorts after every ranked word in its band. The ranks come from
  `content/frequency/` via `tools/content-rank/`, ordered by how likely an A1–A2 learner already knows the word.
- **20 new words per sitting is the ceiling**, set by the default 600 s session budget, not by the cap. Raising
  `new_per_day` to 200 allows more sittings per day; it does not make a sitting longer. This is expected, not a
  failed setting.
- For a word you already know: reveal, then grade **Easy** (`Sin dudar`). Easy graduates the new card with a
  multi-day interval. About 10 sittings sweep 200 words.
- Graduated words return as reviews within days (plan estimate: 4–7). If new words stop appearing while those reviews
  pile up, the backlog threshold (default 150) is suppressing them; raise it:
  `learn settings --username <owner> --backlog-threshold 400`.
- After calibration, set the cap back: `learn settings --username <owner> --new-per-day 20` (12 is the default).

Signal to stop sweeping: if the first sittings are almost all Easy, the approved set is too easy. Approving more A1
does not help; get A2/B1 approved (reviewer first, §4).

### Fallback: `learn seed-known`

Run this **after** approval and **after** `learn settings`, dry-run first. For when tapping through hundreds of known
words is not worth it. Marks the top N **approved** words in rank order as already known: both directions go to
Review, stability 30, due in 30 days, without spending today's new-word budget.

```bash
docker exec palabras /app/Palabras learn seed-known --username <owner> --top 100 --dry-run   # keys, then dry-run=100
docker exec palabras /app/Palabras learn seed-known --username <owner> --top 100             # seeded_words=100 seeded_cards=<n>
docker exec palabras /app/Palabras learn seed-known --username <owner> --top 100 --reset     # reset_words=100 reset_cards=<n>
```

`seeded_cards` counts **cards touched**, not words: two per sense (both directions), so it can exceed `seeded_words`.
`--reset` prints `reset_words=<n> reset_cards=<m>` and puts the cards back to New, due now.

This is **one-shot, not idempotent in the useful sense**: a plain re-run rewrites every selected card to
`Reps=1/Stability=30/LastReviewMs=now` and recomputes the due date from now, including cards the learner has
genuinely reviewed (so it *postpones* them); `--reset` wipes `Reps`/`Stability`/`LastReviewMs` altogether. Run it
once, choose N so it covers only words not yet studied, and do not replay it "to be sure".

`seeded_words=N seeded_cards=0` with exit code `0` is the trap: **there was nowhere to seed**. It is what you get
when the owner account was created after approval, so the approved words have no cards. It also prints this on a
database with no cards at all — a silent no-op. Fix the cards first by replaying the saved keys (§4, repair path),
then dry-run again; `--dry-run` in that state still prints the N keys plus `dry-run=N`, which makes the no-op easy to
miss.

Cautions:

- It is a guess, not a measurement: a wrongly seeded word disappears for 30 days (`--due-days` changes the due date;
  it does not change stability, which stays 30).
- It overwrites the SRS state of every selected card, including cards already graded in a sitting, and `--reset`
  sets them back to New. Run it before sweeping, or choose N so it covers only words not yet studied.
- It only selects approved words. If the approved set is exhausted, seeding cannot help; approve the next batch.
- Suspended cards keep their suspension.

## 6. Daily use

Owner (learner), at `https://palabras.siebentower.nl` on Android Chrome:

- The session page (`/`, `/sesion`) composes a plan of due reviews plus up to 20 new words, new ones in rank order.
  Grade each card Again / Hard / Good / Easy. Answers queue offline and send when online.
- The day strip ("Día y racha") above the session shows today's state (`Hoy, por completar`, `Día completado`,
  `Hoy saltado`), the streak, and **Saltar hoy**: one skip per rolling 7 days, online only, refunded if a session is
  completed the same day. The nightly rollover job ships disabled, so no `missed` days are written.
- `/palabras` lists words, `/progreso` shows progress, `/ajustes` shows the build commit.

Reviewer, with the reviewer account:

- `/revisar`: queue of `pending_review` words with alternatives; approve (also in batches), edit, reject or request a
  change. Each approval writes a `word_reviews` verdict and creates the owner's cards for that word at once.
- `/aprobadas`: approved list, search, demote.
- The owner account gets 403 on these routes; the reviewer gets 403 on learner routes. Details: `09-review-api.md`.

## 7. Troubleshooting

| Symptom | Cause | Remedy |
| --- | --- | --- |
| No new `deploy.log` lines after the push; `/healthz` still `0c5afc3` | poll paused, unscheduled, or refusing (tracked edits, non-fast-forward) | read `tail -n 50 "$DEPLOY/deploy.log"`; remove `$DEPLOY/paused` only if intended; enable the User Script; run it once by hand (`bash /boot/config/plugins/user.scripts/scripts/palabras-auto-deploy/script`); causes in `08-upgrade-rollback.md` §E.3 |
| `deploy FAILED, rolled back`, repeating every 5 min | candidate never served `/healthz`, often a migration failure | `touch "$DEPLOY/paused"`, then check `ReviewGate` state on a scratch copy per `08-upgrade-rollback.md` §G.3; partial migration → §8 |
| `rollback FAILED` | previous image could not be restored | pause at once, keep container logs and the data set, §8 and `08-upgrade-rollback.md` "Rollback" step 2 |
| Login impossible | account missing or wrong password, or the account guard failed | `user list`; `user set-password <name>` or `user create …` (`04-accounts.md`); reviewer and owner are different logins |
| Owner sees no cards; `content status` shows approved words | owner account created after approval (cards are made only for learners existing at approval time) | replay the saved key list by name (keys from the `--dry-run` record): `content approve <key…>` reports them `unchanged` and runs the card pass for every named word — **verified**: 10 cards restored, `approved=0 unchanged=5`, idempotent on re-run. One unknown key aborts the whole call, so the list must be exact. Do **not** re-run `--top N` (approves the next N) or `--all-pending` (bulk: approves the entire remaining corpus) |
| `learn seed-known` reports `seeded_words=N seeded_cards=0`, exit 0 | nowhere to seed: the approved words have no cards (often the owner account was created after approval), or the database has none | repair cards first with the saved key list (§4), then dry-run again; the command itself changes nothing (§5) |
| Owner sees no cards; approved=0 | nothing approved yet, or imported after the approve | import, then §4 approval |
| New words stop while reviews pile up | backlog above `backlog_threshold` | `learn settings --username <owner> --backlog-threshold 400` |
| Only 20 new per sitting despite `--new-per-day 200` | by design (600 s budget) | start another sitting |
| Over-approval (`--top` run twice, or `--all-pending`) | `--top` advances to the next pending words | identify the extra keys from the two dry-run lists or `/aprobadas`; reviewer demotes them in `/aprobadas`, or `content retire <key…>` takes them out (bring back later with `content approve <key>`). Both invalidate their cards; studied progress on them is suspended, not deleted |
| `content approve` fails with "database is locked" | CLI approval races an import or reviewer batch | re-run; nothing was partly written (`06-content-cli.md`) |

## 8. Rollback

Rollback is by image and data files only. **Never run a Down migration** (`ReviewGate.Down` drops `word_reviews` and
six `words` columns). Retain the outgoing image **before** the deploy replaces it (§1 item 5): read
`docker inspect palabras --format '{{.Image}}'` and tag that ID while the old container still runs. Tagging after the
deploy is too late — the poll will already have swapped the container, and only its own preserved copy remains. The
poll also tags the outgoing ID as `palabras:rollback-<image-id12>` (newest two kept, listed in
`$DEPLOY/rollback-images`); either tag is a valid anchor. If both are missing, do not prune images and follow
`08-upgrade-rollback.md` §E.4 step 1.

**R-B is the rollback**, not the poll's automatic image rollback: the poll restores only the image and can report old
health while the database stays migrated or partially migrated. R-B restores the stopped database set, its own
`-wal`/`-shm`, `keys/` and the retained old image together.

**Old image on the migrated database (R-A).** This is what the poll's automatic rollback leaves running. The new
columns are nullable and `word_reviews` is an extra table, so it is expected to work, but `08-upgrade-rollback.md`
§G.4 allows R-A only after the drill, and **never on a partially applied `ReviewGate`** (§G.3).

**A new reviewer browser tab does not work against the rolled-back old server.** The old server returns an empty
review queue and 404s the newer review endpoints, while the current tab expects `pendingTotal`/`topics` and posts to
`/api/review/actions`: the tab reports invalid-response errors and its actions fail. The queue marks those
non-retryable errors failed, not applied. Preserve the queue, stop reviewing, return to the new image, then inspect
and retry explicitly. Do not claim reviewer functionality across versions, and do not clear browser data to hide it.

**Restore the pre-upgrade data set (R-B)** — the safe default; loses everything written after the snapshot (on this
deploy: the accounts' logins since, any import/approval/progress):

```bash
touch "$DEPLOY/paused"
cd "$DEPLOY" && docker compose down --timeout 60
# Restore palabras.db together with ITS OWN -wal/-shm and keys/ from the same
# stopped set $DEPLOY/bringup-<stamp>/ (§1.4) into /mnt/cache/appdata/palabras/ (never mix sets);
# chown 1654:1654, chmod 600 on the database files.
docker tag palabras:pre-<stamp> palabras:local   # or the poll's palabras:rollback-<image-id12>
docker compose up -d --no-build --force-recreate
curl -s http://127.0.0.1:18480/healthz        # commit is the old one
docker exec palabras /app/Palabras user list  # both accounts
```

Full procedure, file modes and acceptance: `08-upgrade-rollback.md` "Rollback" (R-B) and §G.3–G.4. Keep the poll
paused afterwards; resume only with a fixed commit and a fresh pre-upgrade data set.

**Content or approval went wrong but the app is healthy:** do not restore the database. Fix forward with §7.
