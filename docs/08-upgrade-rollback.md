# Upgrade, rollback and migration-lock recovery (owner-run)

Owner-run procedures for deploying a commit that adds EF migrations to the production `palabras` container, rolling
it back, recovering from an abandoned EF migration lock, and restoring a data directory the Unraid mover emptied. No
agent or automation runs these. Nothing in this file has been executed against production as written; the
old-image compatibility drill (step 6) has not been run either. Section C follows the restore that succeeded on
2026-10-18 (same order and ownership), but its exact commands were not run.

Related: automatic deploy, pause and resume in `README.md` ("Automatic updates, pause and rollback"); restoring
progress from a JSON export in `restore-drill.md`; CLI behaviour in `06-content-cli.md`.

## Facts this procedure depends on

- The app migrates before it serves: `Program.cs:125` awaits `DatabaseLifecycle.InitializeAsync`, which calls EF
  `MigrateAsync` (`DatabaseLifecycle.cs:7-13`). No backup is taken first. The nightly backup service starts only
  after startup, so it never protects the first migration.
- `/healthz` opens the configured SQLite file and runs `SELECT 1`; when that fails it answers HTTP 503 with a JSON
  body naming the `database` component. Both bodies carry the build identity (`version`, `commit`, `milestone`; E). The compose healthcheck and the deploy poll therefore cannot report a
  database-less container as healthy. Startup itself is unchanged: a missing database file is still created and
  migrated before the app serves, and that fresh, account-less database answers 200.
- Every CLI verb except `import --dry-run` and `restore-progress` also migrates, **including `content status`**
  (`06-content-cli.md`, CLI rules). The CLI from a newer clone upgrades whatever database it points at.
- The Unraid compose (`deploy/unraid/docker-compose.yml:6-9`) builds `palabras:local` from the host clone. Rebuilding
  `palabras:local` from an older commit is **not** a rollback: base images use floating tags (`Dockerfile:1,8,17`),
  so the result can differ from what ran. The only exact rollback target is the image ID the container was
  actually running. The rebuild-based rollback in `README.md` is a source rollback, not an image rollback.
  Since T-20261018-2105 the poll itself tags the image the container is running as
  `palabras:rollback-<image-id12>` before every rebuild (`:348-372`), idempotently, keeping the newest two tags
  (`ROLLBACK_KEEP=2`, manifest `$DEPLOY/rollback-images`, `:38-40`, `:374-419`) — so a poll rebuild no longer leaves its
  outgoing image untagged. A running image recorded in `$DEPLOY/failed.image` is never tagged (section F). That
  does not retroactively cover an image that was already untagged before the poll ran, and `docker image prune -a`
  still removes an unused tagged image.
- The poll (`deploy/unraid-deploy.sh`) rebuilds and recreates the container whenever the container is not healthy,
  the running container's image id is not the one the recorded sha deployed, or `origin/main` moved (`:584-626`).
  A stopped container therefore gets rebuilt within five minutes unless polling is paused. The pause file is
  checked only after the run takes its lock (`:49-50`, `:513-516`), so a run already in progress finishes. Since
  `884d663` a rebuilt candidate that does not serve `/healthz` within a bounded wait is replaced by the image that
  ran before the rebuild (section F); the database is not restored.
- Poll data guards, all enforced only once `deployed.sha` exists. The data directory is `APPDATA_PATH` parsed from
  `$DEPLOY/.env` (`:117-135`, `:519-525`). (1) Before any fetch, a missing, symlinked, non-regular or 0-byte
  `palabras.db` there aborts the run without writing `deployed.sha` (`:142-170`, `:526`); nothing is recreated.
  (2) Before every rebuild, `palabras.db`, its `-wal`/`-shm` and `keys/` are copied with `cp -a` to
  `$DEPLOY/backups/<UTC stamp>-<sha7>/`, newest 5 kept; any copy failure aborts (`:176-237`, `:640`). The copy is
  taken without stopping the app, so it may need WAL recovery; prefer A step 6 for an upgrade. (3) After a rebuild
  and on every unchanged healthy poll, `docker exec palabras dotnet /app/Palabras.dll user list` must print a `ROLE`
  header and at least one account, and no fewer than the pre-rebuild count; otherwise the poll exits non-zero and
  keeps the previous `deployed.sha`, even with the container healthy (`:241-284`, `:610`, `:671`). Without
  `deployed.sha`, guard 1 is skipped and zero accounts is only a warning, so deleting it disables both for that run.
- Recreating the container sends SIGTERM, then SIGKILL after the stop timeout. A SIGKILL during a migration can leave
  the EF migration lock behind (branch B below).
- Live files, inside the container under `/data`: `palabras.db`, `palabras.db-wal`, `palabras.db-shm`,
  `palabras.db.schema.lock` (app lock file, no user data), `keys/` (data-protection keys), `backups/`. The app runs
  as UID/GID 1654.
- `SrsCards.Down` is **not a data-preserving rollback.** It drops `review_log`, `user_settings` and `cards` and the
  `users.timezone` column (`Backend/Palabras/Persistence/Migrations/20261018000241_SrsCards.cs:189-203`). Never run a
  down-migration to roll back. Roll back by restoring files and image, as below.

Tools needed on the host: `docker`, `sha256sum`, `pgrep`, and a `sqlite3` CLI. Whether the Unraid host has `sqlite3`
was not checked; provide one before starting. This runbook does not install anything.

## A. Pre-migration backup and upgrade

**Trigger:** deploying any commit that adds a migration under `Backend/Palabras/Persistence/Migrations/` (for
example the first deploy containing `ContentSchema` and `SrsCards`).

**Preconditions:** downtime agreed; the production container exists (running or stopped) so its image ID can be
read; free space checked in step 4; a verified restore point exists before the migration (step 6, and section F
"Backup expectation"). Production had no backup of any kind until 2026-10-18; never deploy without one again.

### Steps

1. **Set variables.** Choose a safe directory **outside** the data bind mount.

   ```bash
   DEPLOY=/mnt/user/docker/palabras
   REPO=/mnt/user/repos/palabras
   STAMP=$(date -u +%Y%m%dT%H%M%SZ)
   SAFE=/mnt/user/<owner-chosen-share>/palabras-preupgrade-$STAMP
   ```

2. **Pause the poll and wait for any run in progress.**

   ```bash
   touch "$DEPLOY/paused"
   flock -w 600 /var/lock/palabras-deploy.lock true   # returns once no deploy run holds the lock
   tail -n 5 "$DEPLOY/deploy.log"                      # last line should be "paused; no fetch or rebuild" or a finished run
   ```

3. **Retain the running image under a unique tag.** Read the ID from the container, not from `palabras:local`.

   ```bash
   IMAGE_ID=$(docker inspect palabras --format '{{.Image}}')
   SHA=$(cat "$DEPLOY/deployed.sha")
   ROLLBACK_TAG=palabras:rollback-${SHA:0:12}-$STAMP
   docker tag "$IMAGE_ID" "$ROLLBACK_TAG"
   docker image inspect "$ROLLBACK_TAG" --format '{{.Id}}'   # must equal $IMAGE_ID
   docker image inspect palabras:local --format '{{.Id}}'    # if different, palabras:local was rebuilt; the container's ID wins
   ```

   `deployed.sha` is the last SHA the poll saw healthy. It need not match the image if someone ran
   `docker compose up --build` by hand; record both. Do not prune this tag until the upgrade is accepted.

4. **Resolve the host data path and check free space.**

   ```bash
   DATA=$(docker inspect palabras --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Source}}{{end}}{{end}}')
   echo "$DATA"                                   # expected per README: /mnt/cache/appdata/palabras
   du -ch "$DATA"/palabras.db* "$DATA"/keys | tail -n 1
   df -h "$DATA" "$(dirname "$SAFE")"
   ```

   The filesystem holding `$SAFE` needs at least three times the `du` total (preserved set, scratch copy,
   consolidated copy). The data filesystem must also keep room for the migration and its WAL.

5. **Stop every database user.**

   ```bash
   cd "$DEPLOY"
   docker compose down --timeout 60
   pgrep -af 'Palabras'                           # expect no output: no app, docker exec CLI or host dotnet CLI
   ls -la "$DATA"
   ```

   Do not run any CLI verb from the clone during the window, not even `content status`: once the clone is updated,
   it migrates the database.

6. **Preserve and check the pre-migration set.** The `set/` copy stays untouched; checks run on `scratch/`, because
   opening a WAL database with `sqlite3` can change its files.

   ```bash
   mkdir -m 700 "$SAFE" "$SAFE/set" "$SAFE/scratch"
   cp -a "$DATA/palabras.db" "$SAFE/set/"
   for f in "$DATA/palabras.db-wal" "$DATA/palabras.db-shm"; do [ -e "$f" ] && cp -a "$f" "$SAFE/set/"; done
   cp -a "$DATA/keys" "$SAFE/set/keys"
   (cd "$SAFE/set" && find . -type f -exec sha256sum {} + > "$SAFE/set.sha256")
   cp -a "$SAFE/set/." "$SAFE/scratch/"
   sqlite3 "$SAFE/scratch/palabras.db" 'PRAGMA integrity_check;'      # must print exactly: ok
   sqlite3 "$SAFE/scratch/palabras.db" 'SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY 1;'
   sqlite3 "$SAFE/scratch/palabras.db" "SELECT name FROM sqlite_master WHERE name='__EFMigrationsLock';"
   sqlite3 "$SAFE/scratch/palabras.db" "VACUUM INTO '$SAFE/palabras-preupgrade-$STAMP.db';"
   sqlite3 "$SAFE/palabras-preupgrade-$STAMP.db" 'PRAGMA integrity_check;'   # must print: ok
   ```

   If `__EFMigrationsLock` exists, `SELECT * FROM "__EFMigrationsLock";` must return no rows; otherwise go to branch
   B before upgrading. If either integrity check is not `ok`, stop: do not upgrade on top of a damaged database.
   Never copy anything over the live `palabras.db-wal`/`-shm`.

7. **Optional, decides rollback option R-A: old-image compatibility drill.** Isolated containers with other names,
   loopback ports and copies only; the production container and `palabras:local` are not touched.

   ```bash
   (cd "$REPO" && git fetch origin main && git merge --ff-only origin/main)
   docker build -t palabras:candidate-$STAMP "$REPO"
   mkdir -m 700 "$SAFE/drill" && cp -a "$SAFE/set/." "$SAFE/drill/" && chown -R 1654:1654 "$SAFE/drill"
   docker run -d --name palabras-drill-new -p 127.0.0.1:18498:18480 -v "$SAFE/drill:/data" palabras:candidate-$STAMP
   curl --fail http://127.0.0.1:18498/healthz     # retry until ok: the copy is now migrated
   docker rm -f palabras-drill-new                # the copy was used only for this drill
   docker run -d --name palabras-drill-old -p 127.0.0.1:18499:18480 -v "$SAFE/drill:/data" "$ROLLBACK_TAG"
   curl --fail http://127.0.0.1:18499/healthz
   ```

   Then check password login against port 18499 using the `login.json` method in `restore-drill.md` (expect 200),
   and remove the drill container with `docker rm -f palabras-drill-old`. If both checks pass, R-A is allowed. If
   they fail or the drill was skipped, only R-B is allowed. `docker rm -f` kills; acceptable here only because the
   drill database is a disposable copy.

8. **Upgrade with the poll still paused.** Running it by hand prevents the poll from re-creating a failing container
   every five minutes.

   ```bash
   (cd "$REPO" && git fetch origin main && git merge --ff-only origin/main && git rev-parse HEAD)
   cp "$REPO/deploy/unraid/docker-compose.yml" "$DEPLOY/docker-compose.yml"
   cd "$DEPLOY" && docker compose config --quiet && docker compose up -d --build
   docker logs -f palabras                        # watch migrations finish; Ctrl-C stops only the log view
   ```

   If `/healthz` is not reached within about three minutes (the poll's default gate, `PALABRAS_HEALTH_TIMEOUT=180`,
   `deploy/unraid-deploy.sh:83`; section F) and the log stops at the migration lock, go to branch B. Any other
   failure: go to rollback. This manual upgrade bypasses the poll, so the poll's automatic image rollback does not
   apply to it.

### Verification (acceptance)

All must pass; health alone is not acceptance.

- `docker inspect palabras --format '{{.State.Health.Status}}'` prints `healthy`, and
  `curl --fail http://127.0.0.1:18480/healthz` succeeds.
- `curl --fail https://palabras.siebentower.nl/healthz` succeeds, and the owner logs in through HTTPS in Android
  Chrome.
- Learner progress is present: the session plan and words list load. If the pre-upgrade database already had
  `cards`, the JSON export's card and review counts equal `SELECT count(*) FROM cards;` and
  `SELECT count(*) FROM review_log;` on `$SAFE/scratch/palabras.db`.
- One successful backup: after the next 03:30 learner-local run, `docker logs palabras 2>&1 | grep 'SQLite backup
  published'` shows a line and the file exists in `$DATA/backups/`. There is no on-demand backup trigger.
- For a release that adds migrations (M7 `DayStatusAndSkips` onwards): a published backup line alone does not prove
  the new tables were copied, because the backup's own schema check only requires the six older tables
  (`BackupService.cs:99-101`). Open the newest file in `$DATA/backups/` read-only (it is a standalone copy, not the
  live database) and compare its table list and `__EFMigrationsHistory` with `$SAFE/scratch/palabras.db` after the
  migration. Copy-pasteable check and its prerequisites: D.7.

After acceptance: write the deployed SHA so resuming does not rebuild an unchanged commit
(`git -C "$REPO" rev-parse HEAD > "$DEPLOY/deployed.sha"`), then `rm "$DEPLOY/paused"`. Keep `$SAFE` and the rollback
tag at least until the backup check has passed.

### Record

Write these down (outside the repo) before resuming the poll:

- running image ID before upgrade, rollback tag, previous `deployed.sha`, new commit SHA, new image ID;
- resolved host data path; `$SAFE` path; `palabras-preupgrade-<stamp>.db` filename; `set.sha256`;
- integrity-check results for both copies and the migration history list;
- old-image drill result (passed / failed / skipped); acceptance results, including the first backup filename.

## Rollback

**Trigger:** upgrade acceptance fails, or branch B cannot clear the lock safely. In a new shell, set `DEPLOY`,
`DATA`, `SAFE`, `ROLLBACK_TAG` and `IMAGE_ID` from the record.

1. Keep the poll paused (`touch "$DEPLOY/paused"`). Stop the app and confirm no users: `cd "$DEPLOY" && docker
   compose down --timeout 60 && pgrep -af 'Palabras'` (no output).
2. Preserve the failed state as evidence, the same way as step 6 of A, into `$SAFE/failed-$(date -u
   +%Y%m%dT%H%M%SZ)/`, including `-wal`, `-shm` and `keys/`. Also save `docker logs palabras` output if the
   container still exists.
3. Choose one:
   - **R-A, old image on the migrated database.** Only if drill step 7 passed. Leave the database files as they are.
   - **R-B, pre-upgrade database and old image.** Restore the verified set: remove the failed `palabras.db`,
     `palabras.db-wal` and `palabras.db-shm` from `$DATA` (already preserved in step 2), then copy
     `$SAFE/set/palabras.db` and its own `-wal`/`-shm` together, or the consolidated
     `palabras-preupgrade-<stamp>.db` alone as `palabras.db` with no sidecars. Never mix sidecars from different
     sets. Restore `keys/` from `$SAFE/set/keys` if it was changed. Progress made after the snapshot is lost.
   ```bash
   chown 1654:1654 "$DATA"/palabras.db* && chmod 600 "$DATA"/palabras.db*
   ```
4. Start the retained image **without building**:

   ```bash
   docker tag "$ROLLBACK_TAG" palabras:local
   cd "$DEPLOY" && docker compose up -d --no-build --force-recreate
   docker inspect palabras --format '{{.Image}}'   # must equal the recorded pre-upgrade IMAGE_ID
   ```

5. Verify as in A's acceptance: health, HTTPS login, learner progress, and one successful backup (the backup service
   exists only in images that contain M10; on an older image this check does not apply, so record that).
6. Keep the poll paused. With `deployed.sha` unchanged and the container healthy, a resumed poll does nothing, but
   the next commit on `main` rebuilds and re-runs the migration. Resume only when a fixed commit is ready and a fresh
   pre-migration set (A step 6) exists. Record which option was used and the results.

## B. Abandoned EF migration lock

**Trigger:** after a start or recreate, the app never reaches `/healthz`; Docker health stays `starting` or turns
`unhealthy`; `docker logs palabras --tail 50` ends with EF's message about acquiring the migration lock and nothing
follows for minutes. The app does not crash, so `restart: unless-stopped` does not help.

**Why the app's own lock does not cover it:** `DatabaseLifecycle` serialises schema work with an OS file lock on
`palabras.db.schema.lock` (`DatabaseLifecycle.cs:51-69`), which the OS releases when a process dies. EF Core's
SQLite provider keeps a **separate** lock as a row in the `__EFMigrationsLock` table inside the database. It inserts
the row, waits in a loop until it gets it, and deletes it on release, with no stale-owner timeout. A process killed
mid-migration leaves the row, and every later migration waits forever. See Microsoft's
[SQLite limitations: handling abandoned locks](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations#handling-abandoned-locks).

**Rule:** never clear a lock that another process may still hold, and never script or schedule automatic clearing.
A row is abandoned only when no app, `docker exec` CLI or host CLI process exists for this database.

### Steps

1. Pause the poll (A step 2). Otherwise it recreates the unhealthy container, and the SIGKILL that follows can strand
   the lock again.
2. Save the logs: `docker logs palabras > "$SAFE/stuck-startup.log" 2>&1` (create `$SAFE` as in A step 1).
3. Stop every process that could hold the lock: `cd "$DEPLOY" && docker compose down --timeout 60`, then
   `pgrep -af 'Palabras'` must print nothing. If something remains, a migration may be active: let it end or stop it
   gracefully. Do not touch the database while any such process exists.
4. Preserve the database, sidecars and keys exactly as in A step 6 (`set/`, `set.sha256`, `scratch/`).
5. On `scratch/`: run `PRAGMA integrity_check;` (must be `ok`), list `__EFMigrationsHistory`, and run
   `SELECT * FROM "__EFMigrationsLock";`. Compare the history with the migration files of the image being started
   (`Backend/Palabras/Persistence/Migrations/`; on `main` at `c3ce543`: `InitialAccounts`, `ContentSchema`, `SrsCards`, `DayStatusAndSkips`, `RolloverCutoverMarker`). A
   committed prefix is expected: each migration runs in a transaction, so an interrupted one is rolled back and
   re-applied on the next start. If the integrity check fails or the history contains IDs the image does not know,
   do not clear the lock; use rollback R-B.
6. Confirm the live files are still the verified ones: `(cd "$DATA" && sha256sum -c "$SAFE/set.sha256")` must
   report `OK` for every file. Then clear the row on that database, with every process still stopped and the
   sidecars in place:

   ```bash
   sqlite3 "$DATA/palabras.db" 'DELETE FROM "__EFMigrationsLock";'
   sqlite3 "$DATA/palabras.db" 'SELECT count(*) FROM "__EFMigrationsLock";'   # must print 0
   chown 1654:1654 "$DATA"/palabras.db* && chmod 600 "$DATA"/palabras.db*      # sqlite3 as root may create root-owned sidecars
   ```

7. Start the same image without rebuilding: `cd "$DEPLOY" && docker compose up -d --no-build`. Watch
   `docker logs -f palabras` until migrations finish, then run A's acceptance checks.
8. If it hangs at the lock again, the cause is not an abandoned row. Stop, keep the poll paused, and use rollback
   with the retained image.

### Record

Stuck log excerpt, lock row contents (`Id`, `Timestamp`), migration history before and after, `set.sha256` path,
integrity result, who cleared the row and when, and the acceptance results.

## C. Data directory emptied by the Unraid mover

**What happened on 2026-10-18:** the `appdata` share used cache mode Yes (cache → array) and the container binds only
the cache path (`APPDATA_PATH=/mnt/cache/appdata/palabras`). The nightly mover moved `palabras.db` and `keys/` to the
array (`/mnt/disk2/appdata/palabras`) while the app ran. After the next start the app found an empty `/data`, created a
fresh database with zero accounts and new keys, and the poll kept reporting success. Nothing was deleted; the original
files sat on the array.

**The poll's guards detect this condition but cannot prevent it.** Only the storage policy in step 7 stops it.

**Trigger,** any of:

- the owner's correct password returns 401, or `docker exec palabras dotnet /app/Palabras.dll user list` prints the
  `ROLE` header with no rows;
- the container is `healthy` and `/healthz` answers 200 (fresh database) or 503 (file vanished under the running
  app); logs show `unable to open database file` (SQLite error 14);
- `ls -la "$DATA"` shows no `palabras.db`, or one (and a `keys/` file) created after the last known good login, while
  `deployed.sha` names a deploy the poll recorded as healthy;
- `deploy.log` shows `data guard FAILED (poll)` or `ZERO accounts`;
- `grep -E 'mover: (started|finished)' /var/log/syslog` shows a mover run between the last good login and the failure.

**Preconditions:** `DATA` set as in A step 4 (the bind source); a `SAFE` directory outside every `appdata` path (A
step 1). Never read or write SQLite through `/mnt/user/appdata/...` (shfs union of cache and array).

### Steps

1. Pause the poll and wait for a running poll (A step 2).
2. **Find the moved data.** The mover writes to an array disk, not to the cache path:

   ```bash
   ls -la /mnt/disk*/appdata/palabras/ /mnt/cache/appdata/palabras/
   SRC=/mnt/disk2/appdata/palabras                # the array copy holding the older palabras.db and keys/
   ```

3. **Verify the candidate before trusting it.** Check a scratch copy (not `$SRC`) with the image's own CLI, which
   prints role, username and timestamps only, never password hashes. The CLI migrates what it opens, hence the copy.

   ```bash
   CHECK=$(mktemp -d)
   cp -a "$SRC/palabras.db" "$CHECK/"
   for f in "$SRC/palabras.db-wal" "$SRC/palabras.db-shm"; do [ -e "$f" ] && cp -a "$f" "$CHECK/"; done
   chown -R 1654:1654 "$CHECK"
   docker run --rm --network none -v "$CHECK:/data" palabras:local user list   # expect the owner and reviewer rows
   rm -rf "$CHECK"
   ```

   Do not dump the `users` table or print `password_hash`. If the expected accounts are missing, stop and look for
   another copy (`$DEPLOY/backups/`, `$DATA/backups/`, the appdata backup).
4. **Stop the app.** `docker stop -t 60 palabras` (stays stopped; `restart: unless-stopped`), then
   `pgrep -af 'Palabras'` prints nothing.
5. **Swap the files.** Move the fresh set aside (do not delete it), copy the verified set back, fix ownership. Copy
   `-wal`/`-shm` only together with their own `palabras.db`. Do not copy `palabras.db.schema.lock`.

   ```bash
   mkdir -m 700 "$SAFE/displaced"
   mv "$DATA"/palabras.db* "$DATA/keys" "$SAFE/displaced/"
   cp -a "$SRC/palabras.db" "$DATA/"
   for f in "$SRC/palabras.db-wal" "$SRC/palabras.db-shm"; do [ -e "$f" ] && cp -a "$f" "$DATA/"; done
   cp -a "$SRC/keys" "$DATA/keys"
   chown -R 1654:1654 "$DATA/palabras.db"* "$DATA/keys"
   ls -la "$DATA" "$DATA/keys"                    # everything owned by 1654:1654, data directory writable by 1654
   ```

6. **Start and verify.** `docker start palabras`; retry `curl --fail http://127.0.0.1:18480/healthz` until 200;
   `docker exec palabras dotnet /app/Palabras.dll user list` shows the expected accounts; `docker logs palabras`
   since the start has no `SqliteException`; the owner logs in through HTTPS. Restoring the original `keys/` keeps
   existing sessions valid. Keep `$SRC` until step 7 is done and verified.
7. **Apply the standing fix on the host before the next mover run** (Settings → Scheduler → Mover Settings shows the
   schedule). Choose one:
   - **a.** Shares → `appdata` → "Use cache pool" **Only** (or Prefer). This applies to every container's appdata folder,
     so it is the owner's server-wide decision. With Prefer the mover moves array files back to the cache: first move
     the stale `$SRC` copy out of the share (to `$SAFE`).
   - **b.** A dedicated cache-only share for Palabras (for example `palabras-data` on pool `cache`, mode Only). With
     the app stopped, copy the data there with `cp -a`, `chown -R 1654:1654` it, set
     `APPDATA_PATH=/mnt/cache/palabras-data` in `$DEPLOY/.env`, then `cd "$DEPLOY" && docker compose up -d --no-build`
     to recreate the container with the new bind. The poll reads `APPDATA_PATH` from that `.env`, so its guards
     follow the new path.
8. Resume: `rm "$DEPLOY/paused"` and run the wrapper once. Whether it takes the no-change branch or rebuilds, it runs
   the account check; `deploy.log` must show `account check (…): accounts=<n>` with n ≥ 1 and no `ERROR` line.

### Record

Mover window from syslog, `$SRC` path and file times, account list from step 3 (roles and usernames only), `$SAFE`
path, which fix from step 7 was applied and when, and the step 6 and 8 results.

## D. M7 (`DayStatusAndSkips`) image rollback

Owner-run addendum for the release that adds the M7 migrations `20261018183151_DayStatusAndSkips` (tables `day_status`,
`day_skips`, `practice_sessions`, partial index `ix_day_skips_window`) and `20261018201418_RolloverCutoverMarker`
(table `app_meta(key, value)`, which holds the durable rollover cutover under key `m7_rollover_cutover_ms` once the
job has been enabled; `07-learn-api.md` "Rollover and missed days") together with a day-rollover background job
shipped **disabled**. The kill-switch is the setting `M7:DayRolloverEnabled`, in the container the environment
variable `M7__DayRolloverEnabled`, default **false** (`Backend/Palabras/Learn/DayRolloverSettings.cs`). Only a
case-insensitive `true` enables it; absent, empty, `false`, `1` or anything else stays off. On Unraid it would be set
in `$DEPLOY/.env`, which the compose loads via `env_file: .env`; leave it unset for this release. Changing it takes a
restart/recreate (`docker compose up -d --no-build --force-recreate`), no rebuild and no database downgrade.

The schema migrates regardless of the switch: startup applies `DayStatusAndSkips` and `RolloverCutoverMarker` whether
it is on or off. `missed` history is written only while the switch is enabled, and only for the owner account. While
it is off the job never writes the cutover marker either; re-enabling later keeps the original cutover. Enabling it is a separate, deliberate decision, not part of this
release; it is gated on the rollout-risk conditions for enablement and on the backup and drill evidence in D.2 and
D.7. Turning it off later stops future ticks but does not remove rows already written. The rollout-risk verdict
and its ship conditions live in `.opencode/runs/T-20261018-2105/01-m7-rollout-risk.md`. Everything below extends
section A and the rollback section; of the commands, only the old-image drill items have CI-level analogues — none of
this section has been executed against production.

What is special about this release:

- `DayStatusAndSkips.Up()` only creates tables, indexes and check constraints; it alters no existing table and copies
  no data (`20261018183151_DayStatusAndSkips.cs:10-110`). `Down()` is destructive to M7 data: it drops the three
  tables (`:112-123`). `RolloverCutoverMarker` likewise only creates `app_meta`, and its `Down()` drops it. Same rule
  as `SrsCards`: **never** roll back via a down-migration.
- The old image is expected to tolerate the migrated database. The old binary maps no M7 entity and queries no M7
  table; EF validates nothing between model and schema at runtime, and `DatabaseLifecycle.InitializeAsync` still calls
  `MigrateAsync`, which sees every migration the old binary knows already in `__EFMigrationsHistory` and does nothing,
  leaving the extra history row and three tables alone (`DatabaseLifecycle.cs:7-13`). That is the intent; the proof
  must come from the drill in D.2, not from reasoning.
- Because the rollover only writes rows if it is enabled, a rollback of the *image* never needs to remove M7 history:
  with the kill-switch off the three tables are created empty and stay empty, so image rollback (option R-A) and
  database restore (option R-B) both remain data-preserving for M6 progress.

### D.1 Before push/deploy: pause, retain, prove disabled

Work through section A steps 1–6 first with one additional check. In step 3 (retain the running image), also record
the container's actual image ID even if it equals `deployed.sha`'s build; that ID is the only rollback target
(`docker build` from an old commit is not, base images float).

After the upgraded container is healthy and accepted (A acceptance), **before** any rollover enablement, verify the
kill-switch is really off by observation, not by config-reading alone:

```bash
CFG=$(docker exec palabras sh -c 'printenv' | grep '^M7__DayRolloverEnabled=')
echo "$CFG"                                                        # expect empty (unset, default false) or M7__DayRolloverEnabled=false
for i in 1 2 3; do
  sleep 70
  docker exec palabras ls /data >/dev/null 2>&1                    # container alive
done
docker exec palabras dotnet /app/Palabras.dll user list            # normal account listing, not a migration crash
```

Then, on a scratch copy taken the way A step 6 takes one (copy live files out, check the copy, never open the live
WAL database with sqlite3):

```bash
sqlite3 "$SAFE/scratch-after/palabras.db" 'SELECT count(*) FROM day_status;'           # expect 0
sqlite3 "$SAFE/scratch-after/palabras.db" 'SELECT count(*) FROM day_skips;'            # expect 0
sqlite3 "$SAFE/scratch-after/palabras.db" 'SELECT count(*) FROM practice_sessions;'    # expect 0
sqlite3 "$SAFE/scratch-after/palabras.db" 'SELECT count(*) FROM app_meta;'             # expect 0: no cutover while off
```

If these counts grow while the switch reads false — or after any restart — stop and treat the job as enabled; do not
ship until the gate is proven.

### D.2 Old-image compatibility drill (mandatory for R-A on this release)

Section A step 7 is optional per-release; for M7 make it mandatory and add these assertions on the already-migrated
copy before the acceptance runs:

```bash
sqlite3 "$SAFE/drill/palabras.db" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
#   expect the list to include day_status, day_skips, practice_sessions, app_meta alongside the pre-M7 set
sqlite3 "$SAFE/drill/palabras.db" 'SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY 1;'
#   expect exactly five: InitialAccounts, ContentSchema, SrsCards, DayStatusAndSkips, RolloverCutoverMarker … and NOT a sixth row
docker exec palabras-drill-old dotnet /app/Palabras.dll user list
#   expect the ROLE header plus the same owner/reviewer rows the new image printed
```

If the old image logs a `SqliteException` on start, `/healthz` stays non-200, or `__EFMigrationsHistory` gained/lost
rows after the old image ran, the old binary does not tolerate the migrated schema: only R-B is allowed, and R-B
loses all progress written after the A-step-6 snapshot. Keep the drill evidence (log tail + the two query outputs)
with the upgrade record.

**PENDING EVIDENCE — old-image drill not run.** No result for this drill exists yet, locally or on production; the
local backup round trip in D.7 does not substitute for it. Until a result is recorded here (date, retained image ID,
candidate image, outcome of each assertion above), treat R-A as not allowed for this release.

### D.3 Rolling the container back to the previous image while the database is migrated (option R-A)

This is the plain rollback section, option R-A, applied to M7; repeat the essential commands here so the M7 runbook
is self-contained. Preconditions: drill D.2 passed; `$DEPLOY`, `$DATA`, `$SAFE`, `$ROLLBACK_TAG`, `$IMAGE_ID` set from
the upgrade record.

```bash
touch "$DEPLOY/paused"                                  # keep the poll out of the way for the whole window
cd "$DEPLOY" && docker compose down --timeout 60
pgrep -af 'Palabras'                                    # expect no output
# Failed-state evidence first (same as rollback step 2):
mkdir -m 700 "$SAFE/failed-$(date -u +%Y%m%dT%H%M%SZ)"
cp -a "$DATA/palabras.db" "$SAFE/failed-$(date -u +%Y%m%dT%H%M%SZ)/"
for f in "$DATA/palabras.db-wal" "$DATA/palabras.db-shm" "$DATA/keys"; do [ -e "$f" ] && cp -a "$f" "$SAFE/failed-$(date -u +%Y%m%dT%H%M%SZ)/"; done
# Database files stay in place, migrated, untouched:
docker tag "$ROLLBACK_TAG" palabras:local
cd "$DEPLOY" && docker compose up -d --no-build --force-recreate
docker inspect palabras --format '{{.Image}}'           # must print the recorded pre-upgrade $IMAGE_ID
docker logs -f palabras                                 # watch startup finish; no migration output expected
```

Then the checks that prove healthy (see D.4). If they fail, proceed to R-B (restore the pre-upgrade database from
`$SAFE/set/`, per the rollback section) — all M6 progress after the snapshot is lost, so with the rollover disabled
the only data at risk is whatever progress accrued between snapshot and rollback. If `day_status`/`day_skips` ever
held rows (gate misbehaved), those rows are lost with R-B too: preserve the failed state first (step 2 above), and
record their counts before any restore.

### D.4 The exact check that proves the app is healthy after an M7 rollback

All must pass, in this order; failure of any one means not-yet-accepted:

```bash
docker inspect palabras --format '{{.State.Health.Status}}'        # healthy
curl --fail http://127.0.0.1:18480/healthz                         # HTTP 200
docker exec palabras dotnet /app/Palabras.dll user list            # ROLE header + exactly the recorded accounts
# On a fresh scratch copy of the (restored or kept) database:
sqlite3 "$SAFE/scratch-rollback/palabras.db" 'PRAGMA integrity_check;'                  # exactly: ok
sqlite3 "$SAFE/scratch-rollback/palabras.db" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
#   same table set as the drill/base, NOT missing the M7 tables (D.2 list)
sqlite3 "$SAFE/scratch-rollback/palabras.db" 'SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY 1;'
#   entries expected by the running image; if the DB was restored (R-B), the two M7 rows are ABSENT again
sqlite3 "$SAFE/scratch-rollback/palabras.db" 'SELECT count(*) FROM cards;'              # equals the recorded pre-upgrade count
sqlite3 "$SAFE/scratch-rollback/palabras.db" 'SELECT count(*) FROM review_log;'         # idem
docker logs palabras 2>&1 | grep -c 'SqliteException'              # expect 0
curl --fail https://palabras.siebentower.nl/healthz                # HTTP 200 through the standing proxy
# Owner logs in via HTTPS in Android Chrome and opens the session plan.
```

For M7 both success paths are legitimate, record which one you got: (a) database never rolled back — history still
contains `DayStatusAndSkips` and `RolloverCutoverMarker` and the four tables exist, (b) database restored from
`$SAFE/set/` — those history rows and the tables are gone again. Do not "fix" (b) by manually re-applying the migration or re-adding the history row;
let the next real upgrade do it.

### D.5 What must NOT be rolled back or deleted

- The `/data` volume contents and their own bound host directory (A step 4's `$DATA`): `palabras.db` **plus its own
  `-wal`/`-shm`** (only ever together, never mixed across sets, never copied while any process holds the file), and
  `keys/` (data-protection keys; a fresh `keys/` silently invalidates every existing login cookie — the incident C
  class below started exactly as "files vanished under the running app").
- `$SAFE` content: `set/`, `set.sha256`, `palabras-preupgrade-<stamp>.db`, drill/failed copies, and `stuck-startup.log`
  from branch B.
- `$ROLLBACK_TAG` (the image tag created in A step 3) and any other image tagged from the running container: the
  exact running image ID is the only faithful rollback target; anything rebuilt from source is *not* a rollback.
- `deployed.sha`: deleting it disables both poll data guards ("Facts", poll data guards) and must never be used
  to force a deployment through.
- `$DEPLOY/backups/` (the poll's `cp -a` snapshots) — newest 5 kept automatically; do not prune them to free space for
  anything short of a proven-successful next upgrade.
- The EF tables `day_status`, `day_skips`, `practice_sessions`, `app_meta` and their `__EFMigrationsHistory` rows: never drop them
  "to match the old binary", never clear the history row manually. A future upgraded image resumes cleanly from a
  history that already contains `DayStatusAndSkips` (normal EF semantics, per the M7 rollout-risk review
  `.opencode/runs/T-20261018-2105/01-m7-rollout-risk.md` §1) — that is the reason (a) above is a success path, not a defect.

### D.6 Failure classes this deployment already recorded (do not replicate)

- Mover emptied the data directory while the app ran, the app recreated a **fresh, account-less** database, and the
  poll kept reporting success because `/healthz` only shows reached-or-not (`2026-10-18` incident, section C). Health
  200 therefore proves nothing about which database is mounted — section C's account checks, not the health check,
  are the real acceptance.
- The poll's guards only protect once `deployed.sha` exists (section C again): keep that file, never delete it.
- The nightly `BackupService` starts only after the app is serving, so it never protects the *first* migration
  (`docs/08-upgrade-rollback.md:14-16`); the pre-migration preserve in A step 6 exists for exactly that.
- The backup's published-file check does not verify the M7 tables (A acceptance bullet above): always do the table
  comparison on the backup file at least once after this upgrade (D.7).
- `docker compose` recreates the container with SIGTERM→SIGKILL; a SIGKILL during a migration strands the EF
  `__EFMigrationsLock` row (branch B). With the rollover disabled first-tick background writes are off, nothing
  additionally competes with the startup transaction, but branch B rules (never clear a live lock, never script
  clearing) stay in force.

### D.7 Backup evidence and the production backup check

**Verified locally on 2026-10-18 (throwaway directories, synthetic data, commit `a0e4912`; not production).** The
app's own backup (`BackupService.CreateBackupAsync`, a `VACUUM INTO`) was run against a migrated database with two
accounts, 58 imported words, one card, one review and one row in each M7 table:

- the backup file's `PRAGMA integrity_check;` printed `ok`;
- it contained `day_status`, `day_skips`, `practice_sessions`, the partial index `ix_day_skips_window` and all four
  `__EFMigrationsHistory` rows including `20261018183151_DayStatusAndSkips` (four at `a0e4912`; the fifth,
  `RolloverCutoverMarker`, came later in `c7262f8` and was not part of this run);
- the backup restored into a fresh data directory had the same counts and rows as the source for users, words,
  senses, cards, review_log and the three M7 tables; `user list` printed both accounts (owner and reviewer) with
  their original creation times; restarting the CLI on it applied no migration again;
- retention: nine backups in a row left seven files;
- three negative controls on `restore-progress` were refused with exit 1 and no writes: a tampered export
  fingerprint, a different account lineage with identical content, and a missing target database.

**Not proven by that run:**

- the HTTP/auth export surface (`/api/learn/export`, cookie auth): no server was started;
- keys preservation: only the absence of keys from the backup was checked (see below);
- the real production database, its data volume and the real 03:30 nightly run;
- the poll's own `cp -a` snapshot (guard 2) and the `cp -a` preserve in A step 6: neither path was exercised, only
  the app's `VACUUM INTO` backup;
- the old-image rollback drill (D.2, still pending evidence).

**Two hazards the round trip exposed:**

- **The backup does not contain keys.** `$DATA/backups/*.db` holds the database only; `keys/` lives beside the
  database and is never copied into a backup. Restoring a database from `$DATA/backups/` restores accounts, progress
  and M7 rows, but not `keys/`: keep the existing `$DATA/keys` in place, or restore it from `$SAFE/set/keys`. A
  restored database paired with a different or freshly created `keys/` is expected to invalidate every existing login
  cookie (code-derived; not measured). For R-B and rollback, the `$SAFE/set` copy (A step 6) is the one that carries
  `keys/`.
- **A copy of a live WAL database must include the WAL content.** In the local run, a row committed only to the WAL
  was missing from a copy of `palabras.db` alone; the `VACUUM INTO` backup taken while the writer was open did
  contain it. A main-file-only copy therefore silently loses the newest committed rows. Copy `palabras.db` together
  with its own `-wal`/`-shm` (A step 6, with the app stopped), or use a `VACUUM INTO` copy; never copy only
  `palabras.db` while `palabras.db-wal` exists.

**Production check (deploy operator, on the Unraid host).** Run after the first published backup following the
upgrade (A acceptance). Prerequisite: the host needs a `sqlite3` CLI, which has not been checked (see "Tools needed on
the host" above); `command -v sqlite3` must print a path before you start. `$DATA` and `$SAFE` come from the upgrade
record; `$SAFE/scratch/palabras.db` is the scratch copy from A step 6.

```bash
command -v sqlite3                                                  # must print a path; otherwise stop
docker logs palabras 2>&1 | grep 'SQLite backup published'          # at least one line
BK=$(ls -1 "$DATA"/backups/palabras-*.db | tail -n 1); echo "$BK"   # newest backup (names start with the UTC stamp)
mkdir -m 700 "$SAFE/scratch-backup"
cp -a "$BK" "$SAFE/scratch-backup/palabras.db"                      # standalone file, no -wal/-shm; never open the original
B="$SAFE/scratch-backup/palabras.db"
sqlite3 "$B" 'PRAGMA integrity_check;'                              # exactly: ok
sqlite3 "$B" "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
#   must include app_meta, day_skips, day_status, practice_sessions next to cards, review_log, senses, users, words
sqlite3 "$B" "SELECT name FROM sqlite_master WHERE type='index' AND name='ix_day_skips_window';"   # prints the name
sqlite3 "$B" 'SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY 1;'
#   must include 20261018183151_DayStatusAndSkips and 20261018201418_RolloverCutoverMarker
for t in cards review_log; do
  echo "$t backup=$(sqlite3 "$B" "SELECT count(*) FROM $t;") snapshot=$(sqlite3 "$SAFE/scratch/palabras.db" "SELECT count(*) FROM $t;")"
done
#   backup must not be lower than snapshot; higher only by progress made since the snapshot
```

If any check fails, keep `$SAFE` and `$ROLLBACK_TAG`, do not resume the poll, and do not prune `$DEPLOY/backups/`.
Record the backup filename and all outputs with the upgrade record.

## E. Production drift snapshot (measured 2026-10-18) — read before the next upgrade

Owner-run readback of measured state. Nothing here was executed against production as written; every claim cites a
read-only measurement artifact under `.opencode/runs/T-20261018-2105/` or a repo file:line. Primary evidence:
`12-prod-drift.md` (+ `12-prod-drift.log`), supplemented by `08-rollback-drill.md` and `06-backup-roundtrip-evidence.md`.

At measurement time the version string could not be used for drift: `/healthz` returned
`{"status":"ok","version":"0.0.1.0"}` and `<Version>0.0.1</Version>` had been constant since M0, so the running
image exposes no build identity. Drift is determinable from that image, not the app (`12-prod-drift.md` §0, §1).
Since `7d37bba` builds report `version` (`0.7.0`), `milestone` (`M7`) and `commit`/`commitShort` in `/healthz`, in
the startup log line and in Ajustes, and carry the image label `org.opencontainers.image.revision`; the sha is
supplied as `-p:CommitSha=<sha>` (Dockerfile `--build-arg COMMIT_SHA`) and is `unknown` when absent. The Unraid
compose and poll now supply it: `deploy/unraid/docker-compose.yml` passes `build.args` `COMMIT_SHA` and
`deploy/unraid-deploy.sh` exports it from the sha the poll actually builds (`README.md`, "Automatic updates, pause
and rollback"), so a poll build reports its `commit` and the image label `org.opencontainers.image.revision`. The
container running at measurement time predates that wiring, so for the drift below the image id (`93d874b09d4f…`)
is still the anchor until the next poll rebuild; the wiring itself is verified by a local script harness and a local
`docker build`, not yet on the host.

### E.1 The drift

- The running container (`docker inspect palabras`) executes image `sha256:93d874b09d4f…`, **untagged**
  (`RepoTags=[]`), **built 2026-10-17T22:55:07+02:00** (compose-labelled). That bounds its source to M1-era commits
  `0cb23fa`/`a4fe119` (last of three 5-minute-spaced poll builds: 22:35, 22:50, 22:55): accounts + cookie auth +
  login UI only — no M2 content schema, no M5 SRS, no M6 learner API/UI, no M7, and a `/healthz` without the
  database check (`12-prod-drift.md` §1; binary forensics: no `FsrsSharp.dll`, no `content/`, 0 learn assets —
  `08-rollback-drill.md` §5).
- **Distance:** 31–32 commits behind `origin/main` (`42e285d = #55` of 55) and 37–38 behind local `main`
  (`3a6b4c9 = #61`, the 6 unpushed M7 commits) (`12-prod-drift.md` §1, §3).
- **The poll has not rebuilt since 2026-10-17 22:55.** Exactly three compose-built (poll) images exist on the host,
  all from that evening; **zero from 2026-10-18**, across ~31 commits and >24 h. This means the poll is
  **paused, unscheduled, or failing before its rebuild step** — not merely "lagging" (`12-prod-drift.md` §0).
- **Host-only facts that tell those three apart** (inside `/mnt/user/docker/palabras`, not readable from the repo
  container; `12-prod-drift.md` §5):
  1. `ls -la /mnt/user/docker/palabras/` — a `paused` file explains zero rebuilds (`deploy/unraid-deploy.sh:513-516`).
  2. `cat /mnt/user/docker/palabras/deployed.sha` — which sha the poll believes is deployed, and whether it is
     consistent with the running image (see E.3, limitation A).
  3. `tail -n 100 /mnt/user/docker/palabras/deploy.log` — poll cadence, `paused; no fetch or rebuild`,
     `tracked local edits; refusing to deploy`, `origin/main is not a fast-forward`, `data guard FAILED`, or
     `DEPLOY OK`.
  4. User Scripts schedule still enabled, and whether the script is a copy or a symlink to
     `/mnt/user/repos/palabras/deploy/unraid-deploy.sh` (a stale copy runs the pre-guard script).

### E.2 What the next successful deploy is

If the operator pushes local `main`, the poll's full deploy path runs (preserve the running image as
`palabras:rollback-<image-id12>`, newest two kept (E.3 item 5) → guard-2 snapshot → compose mirror → rebuild →
recreate → bounded `/healthz` gate, with automatic image rollback on failure since `884d663` (section F) →
accounts guard; order per `deploy/unraid-deploy.sh:637-671`): a **jump of at least ~37 commits** (37–38 measured
at `3a6b4c9`; local `main` has
moved on since, so count again from the running image's source commit in E.1, e.g.
`git rev-list --count a4fe119..main`) **carrying five migrations** (`InitialAccounts`, `ContentSchema`, `SrsCards`,
`DayStatusAndSkips`, `RolloverCutoverMarker`), the M6 frontend and service worker, and the M7 service. The poll now
also rebuilds when the running image id differs from the recorded one (E.3 item 3), so it does not stay on the old
image silently. A small fix pushed on top of `origin/main` without the M7 commits was measured as a ~32-commit jump
with three migrations (`12-prod-drift.md` §3). This changes the risk relative to a small increment:

- Migrations apply at startup on the live database, with no backup taken by the app first. At measurement time the
  poll had no automatic rollback: a candidate that never turned healthy stayed live and the script only logged
  (`24-m1-upgrade-drill.md` §7). Since `884d663` the poll restores the previous **image** when the candidate fails
  its `/healthz` gate (section F), but the database stays migrated. Recreating the container sends SIGTERM→SIGKILL,
  which can strand the EF migration lock (section B).
- The frontend jump replaces the M1 shell with the M6 learner UI, its IndexedDB attempt queue and the hardened
  service worker; clients holding the old bundle interact with a new backend during rollout (`12-prod-drift.md` §4).
- The M7 rollover gate ships **default-OFF** — `M7__DayRolloverEnabled` absent/`false` keeps the job from writing;
  leave it unset in `$DEPLOY/.env` for this release (section D intro). Enabling it later is a separate, gated
  action (first honest `missed` row cannot exist before 00:05 local on 2026-10-20 for local day 2026-10-19;
  `08-rollback-drill.md` §3).

### E.3 Why the poll currently does nothing — documented limitations (observed, not speculative)

Each of these is stated as measured behaviour of `deploy/unraid-deploy.sh` or recorded evidence; none is a
prediction about cause.

1. **`check_clean` fails first.** The script refuses on any tracked local edit (`deploy/unraid-deploy.sh:100-111`,
   invoked at `:532-533`); the working tree currently has 13 modified tracked files, so the poll fails
   `tracked local edits; refusing to deploy` and keeps the last healthy `deployed.sha` (`12-prod-drift.md` §3).
2. **The fast-forward guard.** With a clean tree, the poll still refuses because local `main` (`3a6b4c9`) is not an
   ancestor of the target `origin/main` (`42e285d`) — `origin/main is not a fast-forward; refusing reset/force`
   (`deploy/unraid-deploy.sh:572-580`). Neither guard path runs a build.
3. **Limitation — the no-op path never checked the running image id (fixed by T-20261018-2105).** At measurement
   time the unchanged-branch condition compared the poll's recorded `deployed.sha` with the target and the
   container's *health*; it never compared the container's running image id (then `deploy/unraid-deploy.sh:306-314`).
   If `deployed.sha` came to name a sha newer than the
   image production actually runs, every subsequent poll logs `no change …; healthy; no rebuild` and the drift
   never self-corrects. The mechanism is repo-verified; **whether it is the actual cause of this drift cannot be
   decided from here** — `deploy.log` and `deployed.sha` are host-only (E.1 item lists; `12-prod-drift.md` §2).
   The poll now records the image id of every accepted deploy in `$DEPLOY/deployed.image` and treats a running
   container whose image id differs from that record as "needs deploy" (`:584-617`), so this class of silent drift
   self-corrects instead of persisting. Two residual gaps are deliberate and logged, not silent: with no image
   record for the target sha (a hand-written `deployed.sha`, or a deployment recorded before this change) the
   match falls back to the tag `palabras:local` and is logged as unverified, and the poll still consults no CI
   (item 4 below).
4. **Limitation — the poll does not consult CI at all.** The only pre-build checks are branch, tree cleanliness,
   data, fast-forward and now the running-image comparison; there is no CI-status check, no build/test preflight,
   and the poll goes straight to `docker compose up -d --build` (`deploy/unraid-deploy.sh:584-657`). At
   measurement time `origin/main` CI was red (run `39154226392` on `42e285d`, per `12-prod-drift.md` §2) and the
   poll would have built it anyway.
5. **Limitation — the pre-upgrade image was untagged (fixed for future rebuilds by T-20261018-2105).**
   `docker compose up -d --build` re-points the single floating tag `palabras:local`; the previously running image
   loses its tag and survives only as a dangling image — exactly today's state of `93d874b09d4f`
   (`12-prod-drift.md` §2, "Image tagging: none"). The poll now
   tags the running image `palabras:rollback-<image-id12>` before it rebuilds (`:348-419`) and keeps the newest two
   (manifest `$DEPLOY/rollback-images`). **Today's image stays untagged until a rebuild happens**, so E.4 step 1
   is still required for it.

### E.4 Ordered safe-deploy procedure (closes the risks; run with the poll paused)

Do steps **in this order**; earlier steps exist to keep later steps recoverable. The underlying runbook sections
are cited, not duplicated. `$DEPLOY`, `$DATA`, `$SAFE`, `$STAMP` as in section A steps 1–4.

1. **Record and retain the RUNNING image id before any rebuild.** The rollback artifact
   `93d874b09d4f` is untagged — **one `docker image prune` from being lost**, and after the next rebuild
   `palabras:local` points at the new build, so re-tagging the floating tag after the fact would silently restore
   a *newer* image than production runs (`12-prod-drift.md` §2; `08-rollback-drill.md` §5). Preserve it now:

   ```bash
   IMAGE_ID=$(docker inspect palabras --format '{{.Image}}')     # expect sha256:93d874b09d4f… today
   docker tag "$IMAGE_ID" palabras:rollback-93d874b0-$STAMP      # name must encode "currently running"
   docker image inspect palabras:rollback-93d874b0-$STAMP --format '{{.Id}}'   # must equal $IMAGE_ID
   docker image inspect palabras:local --format '{{.Id}}'        # today: d3c6b5e0a29f (a newer build!) — container's ID wins
   ```

   Use section A step 3's exact commands; the tag name must encode that this is the currently running image
   (`palabras:local` will change meaning at the rebuild). Never prune until the upgrade is accepted.
   Since T-20261018-2105 the poll does this by itself for rebuilds it performs: it tags the running image
   `palabras:rollback-<image-id12>` and keeps the newest two (`:348-419`). This manual step is still required for
   today's already-untagged artifact and whenever polling is paused; a hand tag whose name does not start with
   `palabras:rollback-` is never touched by the poll's own pruning.
2. **Pause the poll and diagnose first.** `touch "$DEPLOY/paused"`, wait for the lock, then read the host-only
   facts in E.1 (paused marker, `deployed.sha`, `deploy.log`, User Scripts schedule) before touching anything.
3. **Take and verify the `/data` backup, including WAL content, with the app stopped** — section A steps 5–6
   (`docker compose down`, then copy `palabras.db` **with its own `-wal`/`-shm`** and `keys/` into `$SAFE/set/`).
   A main-file-only copy of a live WAL database silently loses the newest committed rows; the app's own
   `VACUUM INTO` copy does read through the WAL, but keys are never in any backup (D.7 hazards, both observed in
   `06-backup-roundtrip-evidence.md` §2, §4).
4. **Confirm the newest backup contains the expected migration history and counts** on the scratch copy: section
   A step 6's `integrity_check` and `__EFMigrationsHistory` queries, plus D.7's production check (M7 tables,
   `ix_day_skips_window`, cards/review_log counts vs the snapshot). If the live DB's history is not a clean
   prefix, stop before upgrading (`01-m7-rollout-risk.md` §1).
5. **Old-image drill (mandatory for R-A on M7):** D.2. A drill on a scratch DB passed locally on 2026-10-18
   (`08-rollback-drill.md` §2), including for the very image `93d874b09d4f` runs today (`:133-135`); that proves
   the code path, not the operator's production artifact — the production-side result is operator evidence.
6. **Deploy** per section A step 8 (merge, mirror compose, `docker compose up -d --build`, watch logs). Verify per
   section A's acceptance: healthy, HTTPS `/healthz`, HTTPS login, learner progress, and the backup/table check
   (A acceptance bullet + D.7).
7. **Only after acceptance:** write `deployed.sha` (A, "After acceptance"), resume the poll (`rm "$DEPLOY/paused"`),
   and only then consider anything else (rollover enablement, pruning). A hand-written `deployed.sha` leaves
   `deployed.image` naming the previous deployment, so the next poll verifies the running container against
   `palabras:local` and logs that match as unverified; to record it exactly, write `deployed.image` as
   `<docker inspect palabras --format '{{.Image}}'> <sha>` in the same step.

**Rollback** stays exactly as written in "Rollback" and D.3/D.4 (R-A/R-B, retained image, `--no-build
--force-recreate`, image-id equality check); do not re-run the failed build to "fix" anything.

### E.5 What only the deploy operator can prove

Everything factual about production that this file cannot check from the repo container (`08-rollback-drill.md`
§8 restated for this drift):

1. The **host-only poll facts** of E.1: the `paused` marker, `deployed.sha`, `deploy.log`, and the User Scripts
   schedule/copy-vs-symlink state.
2. The **real `/data` and `keys/` state**: actual DB contents, migration history, account/card/review counts, and
   the pre-upgrade `$SAFE` set; the running image predates the poll guards' guard commits, and whether the
   2026-10-18 mover incident left a clean migration prefix is unknown from the repo side.
3. The **real nightly rollover tick**: the enabled-gate positive `missed` write has not been observed in any
   container; the only observation window is the real 00:05 local tick on 2026-10-20 (D.1, `08-rollback-drill.md` §4).
4. The **production-side §A acceptance checks**: health through HTTPS, login in Android Chrome, learner progress,
   the first published backup line, and the D.7 backup verification with the host's own `sqlite3`.

Proven only locally and to be labelled as such in any record: the backup round trip on synthetic data
(`06-backup-roundtrip-evidence.md` §0) and the rollback drill on a scratch DB (`08-rollback-drill.md` §2). Neither
has been executed against production.

## F. Poll health gate and automatic image rollback (since `884d663`)

Behaviour of `deploy/unraid-deploy.sh` from commit `884d663`. The host runs it only once that commit is on
`origin/main` and the host checkout has fast-forwarded to it; until then the host runs the previous script, which
left a failed candidate live (`24-m1-upgrade-drill.md` §7). Evidence: `30-poll-health-gate.md` (script harness with
stubbed docker/git/curl, and a real-container run of the gate with test containers, never the production
container). The deploy path has not run through the host's User Scripts schedule.

**Why it exists.** The previous poll replaced the container before health was proven and, on failure, only logged.
The broken candidate stayed live, and each later poll tagged that running candidate into the newest-two
`rollback-*` rotation, so two more polls could untag the last good image (`24-m1-upgrade-drill.md` §7).

### F.1 Deploy sequence

1. Preserve the running image as `palabras:rollback-<image-id12>` and remember it as the rollback target
   (`:637-639`), take the guard-2 backup, rebuild and recreate (`:640-657`).
2. **Gate** (`wait_for_health`, `:429-469`): each attempt reads the container state and restart count. The
   candidate passes only when it is `running` and `curl --fail` on `PALABRAS_HEALTH_URL` returns a body containing
   `"status":"ok"`. A container that is `restarting`, `exited`, `dead`, `removing` or `paused`, or that is running
   but has restarted and is not serving, fails at once as a crash loop. Otherwise the gate retries every
   `PALABRAS_HEALTH_INTERVAL` seconds until `PALABRAS_HEALTH_TIMEOUT` and then fails; it never sleeps past the
   deadline, so a run cannot hang.
3. **Success:** the account check runs (`:671`), then `deployed.image`, `deployed.sha` are written, `failed.image`
   is removed, and `DEPLOY OK` is logged (`:672-685`). The accepted image becomes the rollback target the next
   rebuild preserves.
4. **Failure** (`:662-668`, `rollback_previous_image` `:487-510`): the candidate's image id is written to
   `failed.image`, its last 30 log lines go to `deploy.log`, then `palabras:local` is re-pointed to the previous image
   and the container is recreated with `docker compose … up -d --no-build --force-recreate`. The restored container
   must pass the same gate and the account check; only then the run logs `rollback ok` and exits 1 with
   `deploy FAILED, rolled back`. `deployed.sha` and `deployed.image` keep naming the last healthy deploy. If there is
   no previous image (container was absent), the preserved tag is missing, the recreate fails, or the restored image
   does not serve, the run logs `rollback FAILED …` and exits 1; the candidate may still be live.

**A failed candidate is never retained as a rollback target.** While a running image matches `failed.image`, the
preserve step logs `preserve skipped: … recorded as failed …` and creates no tag and no manifest entry (`:358-361`),
so a failed candidate cannot push the last good image out of the `ROLLBACK_KEEP=2` rotation. The rollback itself
adds no manifest entry: the previous image was already recorded by the preserve step of the same run.

**The rollback restores the binary, not the data.** The restored image starts on the database the candidate may
already have migrated. That is option R-A; on an M7 deploy its compatibility evidence is D.2.

### F.2 Settings and state files

The `PALABRAS_HEALTH_*` variables are read from the environment of the script process (`:65-87`). The script does
not read them from `$DEPLOY/.env`; set them in the User Scripts wrapper that runs the script. A value that is not a
positive whole number falls back to the default with a `WARN` line.

| Variable | Default | Effect |
| --- | --- | --- |
| `PALABRAS_HEALTH_TIMEOUT` | `180` | Seconds for the whole gate, used for the candidate and again for the restored image. |
| `PALABRAS_HEALTH_INTERVAL` | `5` | Seconds between attempts. |
| `PALABRAS_HEALTH_MAX_TIME` | `5` | `curl --max-time` for one attempt. |
| `PALABRAS_HEALTH_URL` | `http://127.0.0.1:18480/healthz` | Endpoint that must serve. Not validated. |

The URL default hardcodes port 18480. The compose publishes `127.0.0.1:${APP_PORT:-18480}`
(`deploy/unraid/docker-compose.yml:20`), so if `APP_PORT` is set in `$DEPLOY/.env`, set `PALABRAS_HEALTH_URL` to
the same port, or every deploy fails the gate and is rolled back.

State files in `$DEPLOY` (`/mnt/user/docker/palabras`):

| File | Content | Written |
| --- | --- | --- |
| `deployed.sha` | last healthy sha | only on `DEPLOY OK` |
| `deployed.image` | `<image id> <sha>` of that deploy | only on `DEPLOY OK` |
| `rollback-images` | preserved `palabras:rollback-*` tags, oldest first, newest 2 kept | before each rebuild |
| `failed.image` | `<image id> <sha>` of the last candidate that failed the gate | on gate failure; removed on `DEPLOY OK` |

### F.3 Reading the outcome in `deploy.log`

| Line | Meaning |
| --- | --- |
| `health ok (post-deploy sha=…)` followed by `DEPLOY OK sha=…` | candidate accepted and recorded |
| `health FAILED (post-deploy …)`, `candidate FAILED the health gate`, `failed candidate recorded:` | candidate rejected; rollback starts |
| `rollback ok (…)` followed by `ERROR deploy FAILED, rolled back sha=…` | previous image restored and serving; contained |
| `rollback FAILED (…)` | **not** contained; the failed candidate may be live |
| `preserve skipped: running image … recorded as failed` | a later poll found the failed candidate running and did not tag it |
| `preserve FAILED` | the run stopped before the rebuild; unrelated to the gate |
| `account check FAILED (post-rollback …)` | the restored image serves but its account check failed |

```bash
grep -n 'DEPLOY OK\|health ok (post-deploy\|candidate FAILED the health gate\|failed candidate recorded\|rollback ok\|deploy FAILED, rolled back\|rollback FAILED\|preserve FAILED\|preserve skipped' \
     /mnt/user/docker/palabras/deploy.log | tail -n 20
cat /mnt/user/docker/palabras/failed.image 2>/dev/null || echo 'no failed candidate recorded'
```

**A failing target is retried every five minutes.** `deployed.sha` is not advanced, so each scheduled run rebuilds
the same target, fails the gate and rolls back again. The poll does not pause itself. Repeated `deploy FAILED,
rolled back sha=<same sha>` lines mean a persistently failing target: `touch "$DEPLOY/paused"`, then work the
failure as in E.4 and the Rollback section. On `rollback FAILED`, pause at once and preserve the container logs and
database set (Rollback step 2) before anything else.

### F.4 What this does not cover

- **Data damage.** A migration that corrupts or truncates data, or a vanished `/data`, is not detected by `/healthz`;
  a fresh account-less database answers 200 (`24-m1-upgrade-drill.md` §5c). Only the data guards and backups in
  "Facts" catch or recover it, and R-B (restore a database set) stays a manual decision.
- **A rollback target that was never tagged.** With no running container before the rebuild (first deploy), or a
  preserved tag removed outside the poll (`docker image prune -a`), there is nothing to restore and the run logs
  `rollback FAILED`. The already-untagged image of E.1 still needs E.4 step 1.
- **A broken candidate that answers 200.** The gate proves the app serves and reaches its database, not that
  content, SRS or day behaviour is correct.
- **CI.** The poll still builds a commit whose CI is red (E.3 item 4).
- **A compose-level rollback on the host.** The rollback's `docker compose … up -d --no-build --force-recreate` was
  run only against stubbed docker in the harness. The real-container run replaced it with `docker rm` and
  `docker run`, because the environment it ran in has no compose plugin (`docker: unknown command: docker compose`;
  `30-poll-health-gate.md` §5). The first real automatic rollback through the host's compose is also its first test.
- **A stranded EF migration lock.** Recreating a candidate that is still migrating sends SIGTERM→SIGKILL (section B).
  If the restored image then does not serve, the run logs `rollback FAILED`; clearing the lock stays manual.

### F.5 Backup expectation

Before 2026-10-18, production had no backups: no app backup and no pre-upgrade set. For the M7 jump, the
orchestrator recorded a pre-deploy copy at `/data/backups/palabras-pre-m7-20261018-231805.db` (inside the
container; on the host under `$DATA/backups/`), with `PRAGMA integrity_check` `ok`, 3 migrations, 2 users and
0 words. This docs step did not re-check that file. The same baseline (integrity `ok`, `InitialAccounts`,
`ContentSchema`, `SrsCards`, 2 users, 0 words) was measured on a copy of the live database at 2026-10-18 23:21 CEST
(`31-prod-content-load.md` §2).

Expectation for every future deploy: a verified restore point taken **before** the new image migrates, either the
A step 6 set (database, its own `-wal`/`-shm`, and `keys/`, with the app stopped) or at least a `VACUUM INTO` copy
whose integrity check prints `ok`, recorded with its path, migration list and account count. The poll's guard-2
copy is taken with the app running and is not a substitute for an upgrade (Facts, guard 2). Files under
`$DATA/backups/` sit inside the data directory and do not contain `keys/` (D.7).
