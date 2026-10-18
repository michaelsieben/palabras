# Backup and progress restore drill (M10)

## What is persisted

The single learner is the account with `users.role='owner'` (cookie authentication grants it `learner`). Nightly backup uses that account's `users.timezone`, falling back to `Europe/Madrid` when null, not the container's `TZ`. With no owner, the fallback still applies. At 03:30 local, `BackupService` runs SQLite `VACUUM INTO` against the live WAL database. Missed nights are not replayed on startup; startup at exactly 03:30 runs immediately. Timezone changes take effect when the next nightly schedule is computed.

Backups live next to the configured database: `/data/backups/palabras-<UTC yyyyMMddTHHmmssfffffffZ>-<random GUID>.db`. The timestamp sorts snapshots chronologically; GUID prevents collisions. Keep **7 successful snapshots**, not 7 calendar days. Pruning excludes the just-published snapshot even if the clock moved backward. Unrelated filenames are untouched. Files are written as `.partial`, checked with `PRAGMA integrity_check` and required-table checks, then renamed in the same directory. A failure is logged, temporary output is removed, previous snapshots remain, and the host stays running. Schedule failures retry after one hour; failed nightly runs resume with the next eligible night. A prune error is logged after publication, so extra snapshots may remain.

Protect `/data` and copies: SQLite backups contain password hashes, and exports contain private review answers. Use owner-only directory permissions and encrypted offline storage. These are local backups, **not cloud backup** or protection from total disk loss. Backups do not include `/data/keys`; preserve that directory separately if existing cookies must survive a full-machine recovery. Accounts/passwords are inside SQLite, so new login works without the old keys.

## Export contract

Authenticated learner: `GET /api/learn/export?format=json` or `?format=csv`. Missing/unsupported/repeated formats return 400 `{ "error": "bad_format" }`. JSON v1 includes `userId`, account/content target fingerprint, export time, complete `cards`, and complete `reviewLog`. No account passwords, settings, content payloads, or other users' progress are exported. Instants remain UTC Unix milliseconds; local-date fields remain `yyyy-MM-dd`.

CSV has a `table` discriminator (`cards` / `review_log`) and the union of both tables' camelCase field names, RFC-style quoted cells, and CRLF record endings. Spreadsheet formula prefixes in text are neutralized with an apostrophe. **Only JSON is restorable.**

## Isolated drill: no production changes

Use a copy of a known-good nightly snapshot and a JSON export from the same database lineage. The snapshot must have the exact content IDs/keys/hashes and sense identity data expected by the export. The fingerprint includes the user's immutable creation timestamp and ID plus content identities, not the mutable username/password. An unrelated/newly bootstrapped database is refused. Changed content is also refused: choose a matching baseline rather than editing the JSON fingerprint.

Run from the repo root with .NET 10 installed. Replace the two source paths. Do not point `ConnectionStrings__Default` at production.

```bash
dotnet publish Backend/Palabras -c Release -o /tmp/palabras-drill-app
mkdir -m 700 /tmp/palabras-restore-drill
cp /path/to/palabras-nightly.db /tmp/palabras-restore-drill/palabras.db
cp /path/to/palabras-progress.json /tmp/palabras-restore-drill/progress.json
chmod 600 /tmp/palabras-restore-drill/*
export ConnectionStrings__Default='Data Source=/tmp/palabras-restore-drill/palabras.db'
export DataProtection__KeysPath='/tmp/palabras-restore-drill/keys'
dotnet /tmp/palabras-drill-app/Palabras.dll restore-progress /tmp/palabras-restore-drill/progress.json
# Must exit 0. Repeat: same row counts and state, no duplicate history.
dotnet /tmp/palabras-drill-app/Palabras.dll restore-progress /tmp/palabras-restore-drill/progress.json
dotnet /tmp/palabras-drill-app/Palabras.dll --urls http://127.0.0.1:5089
```

Leave the isolated server in that terminal. In another terminal, create `/tmp/palabras-restore-drill/login.json` with permissions 600 and contents `{"username":"<existing owner username>","password":"<existing owner password>"}`. Never commit that file or put passwords in shell history. Verify:

```bash
curl --fail --silent --show-error -D /tmp/palabras-restore-drill/login-headers.txt \
  -H 'X-Palabras: 1' -H 'Content-Type: application/json' \
  --data-binary @/tmp/palabras-restore-drill/login.json \
  http://127.0.0.1:5089/api/auth/login
rm /tmp/palabras-restore-drill/login.json /tmp/palabras-restore-drill/login-headers.txt
```

Expect HTTP 200 and the owner username/roles. Cookie is `Secure`; this loopback HTTP check proves password login, not a browser HTTPS session. Browser recovery checks require the owner's existing HTTPS proxy. Stop **only the isolated drill server** with Ctrl-C. Automated `M10BackupTests` additionally logs in with the cookie, exports restored progress, and compares cards/history exactly.

Restore is an atomic merge: matching cards are overwritten by the export, missing cards/reviews inserted, identical review IDs/attempt IDs skipped. Conflicting history or IDs abort everything. Rows absent from the export are not deleted. Other users, credentials, settings, and content are unchanged. This is recovery, not synchronization: do not restore an old export into an actively learning database.

## Owner-run real recovery

1. Complete the isolated drill first and record snapshot/export filenames, both command exit codes, and successful login. Keep the original failed database and recovery files unchanged as evidence.
2. Schedule downtime. Disable the owner's deployment poll and stop the production app using the existing owner-operated deployment procedure. Confirm no process holds the target SQLite database. **Never copy a database over a running WAL database.**
3. With the app stopped, preserve the original DB plus its matching `-wal`/`-shm` files together in a separate rollback directory. Place the verified restored drill DB at `/data/palabras.db` in the persisted volume. Do not leave old WAL/SHM sidecars beside a replacement DB. Preserve `/data/keys` and correct file ownership/permissions.
4. Start production using the existing owner procedure, log in through HTTPS, check progress and export it again. On failure, stop the app before restoring the preserved original DB/sidecar set. Re-enable the deployment poll only after acceptance.

No production actions are performed by this implementation or the automated drill.
