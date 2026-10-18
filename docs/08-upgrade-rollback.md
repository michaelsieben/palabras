# Upgrade, rollback and migration-lock recovery (owner-run)

Owner-run procedures for deploying a commit that adds EF migrations to the production `palabras` container, rolling
it back, and recovering from an abandoned EF migration lock. No agent or automation runs these. Nothing in this
file has been executed against production; the old-image compatibility drill (step 6) has not been run either.

Related: automatic deploy, pause and resume in `README.md` ("Automatic updates, pause and rollback"); restoring
progress from a JSON export in `restore-drill.md`; CLI behaviour in `06-content-cli.md`.

## Facts this procedure depends on

- The app migrates before it serves: `Program.cs:125` awaits `DatabaseLifecycle.InitializeAsync`, which calls EF
  `MigrateAsync` (`DatabaseLifecycle.cs:7-13`). No backup is taken first. The nightly backup service starts only
  after startup, so it never protects the first migration.
- Every CLI verb except `import --dry-run` and `restore-progress` also migrates, **including `content status`**
  (`06-content-cli.md`, CLI rules). The CLI from a newer clone upgrades whatever database it points at.
- The Unraid compose (`deploy/unraid/docker-compose.yml:6-9`) builds `palabras:local` from the host clone. Rebuilding
  `palabras:local` from an older commit is **not** a rollback: base images use floating tags (`Dockerfile:1,8,17`),
  so the result can differ from what ran. The only exact rollback target is the image ID the container was
  actually running. The rebuild-based rollback in `README.md` is a source rollback, not an image rollback.
- The poll (`deploy/unraid-deploy.sh`) rebuilds and recreates the container whenever the container is not healthy
  or `origin/main` moved (`:55-66`). A stopped container therefore gets rebuilt within five minutes unless polling is
  paused. The pause file is checked only after the run takes its lock (`:13-33`), so a run already in progress
  finishes.
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
read; free space checked in step 4.

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

   If `/healthz` is not reached within about three minutes (the poll's own limit, `deploy/unraid-deploy.sh:67-80`)
   and the log stops at the migration lock, go to branch B. Any other failure: go to rollback.

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
   (`Backend/Palabras/Persistence/Migrations/`, currently `InitialAccounts`, `ContentSchema`, `SrsCards`). A
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
