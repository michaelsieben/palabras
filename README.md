# Palabras

## What it is

Private vocabulary PWA for learning Peninsular Spanish (Pamplona/Castilian), A2 level, with one learner and one reviewer. M0 is an unauthenticated scaffold, not a usable learning app. Do not expose it publicly yet.

## Screens

One placeholder screen with an API connection badge. Learner and reviewer screens arrive in later milestones. No router yet.

## Stack

React 19 + strict TypeScript + Vite, served by a .NET 10 minimal API in one non-root Linux container. SQLite + EF Core are planned for M2+; no DB exists yet.

## Local dev

Prerequisites: .NET SDK 10.0.401, Node 24.21+, npm, Docker with Compose.

From repo root, start backend:

```bash
dotnet restore Backend/Palabras.slnx
dotnet run --project Backend/Palabras --urls http://localhost:5080
```

In a second terminal:

```bash
cd Frontend
npm ci
npm run dev
```

Open http://localhost:5173. Vite proxies `/api` and `/healthz` to :5080. Backend alone serves the SPA only after a frontend build is copied into `wwwroot` (Docker does this automatically).

Verification from repo root:

```bash
dotnet build Backend/Palabras.slnx
dotnet test Backend/Palabras.slnx
npm --prefix Frontend ci
npm --prefix Frontend run lint
npm --prefix Frontend run build
docker build -t palabras:local .
cp .env.example .env
docker compose config --quiet
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker compose down
```

Expected health JSON: `{"status":"ok","version":"0.7.0","commit":"<40-hex sha>","commitShort":"<7-hex>","milestone":"M7"}`. `/healthz` also opens the configured SQLite file and runs `SELECT 1`; when the database is unreachable it answers HTTP 503 with `{"status":"unhealthy","component":"database",…}` carrying the same identity fields, so the compose healthcheck and the deploy poll cannot report a database-less container as healthy. It does not inspect data: a freshly created database with zero accounts still answers 200 (the deploy poll's account check covers that). `/api/ping` returns only `{"status":"ok","version":…}` and does not check the database. Unknown API paths return 404; other extensionless paths use SPA fallback. Build identity: `version` and `milestone` come from `<Version>` and `<Milestone>` in `Backend/Palabras/Palabras.csproj`, the only place they are set. `commit` is supplied at build time with `-p:CommitSha=<sha>`; the `Dockerfile` forwards `--build-arg COMMIT_SHA=<sha>` to it and also writes the image label `org.opencontainers.image.revision`, and CI passes `$GITHUB_SHA`. Only 7–40 hex characters are accepted; anything else, or no value at all (plain `dotnet run`/`dotnet test`, `docker build .` without the arg), reports `commit` and `commitShort` as `unknown` rather than a guessed sha. Startup logs `Palabras build <version> milestone <milestone> commit <commitShort>.`, and Ajustes shows the same three values read from the live `/healthz`.

## Content authoring

`content/words/`, `content/schema/` and `tools/` are placeholders. Schema, style guide, importer and content lint arrive in M2. No content generation in M0.

## Deploy on Unraid

User authorized server setup and poll-based deployment on 2026-10-17. The private checkout is
cloned and container `palabras` is running healthy. Verified that day: host and container `/healthz`
returned HTTP 200 with `{"status":"ok","version":"0.0.1.0"}`; proxy `/` and `/healthz` both returned
HTTP 200. Manual polls proved first rebuild/healthy deployment and then no-change exit 0.
URLs: https://palabras.siebentower.nl/ and https://palabras.siebentower.nl/healthz.
These are dated deployment checks, not a login-readiness claim: no auth users are configured yet.

### Layout and prerequisites

Verified 2026-10-17: `/mnt/user/repos` exists (shfs user-share path; `/mnt/cache/repos` does not).
It contains separate Git checkouts `SoftwareFactory`, `VaporEngine` and `budgeteer`.
`/mnt/user/docker/palabras` and `/mnt/cache/appdata/palabras` were created during setup.

- Source: `/mnt/user/repos/palabras`, cloned from private GitHub repo.
- Compose project: `/mnt/user/docker/palabras/docker-compose.yml` and adjacent `.env`.
- Persistent data: `/mnt/cache/appdata/palabras`, bind-mounted to `/data`.

The Docker share holds the standalone compose, as requested; it builds from the separate repos
checkout through an absolute `build.context`. The versioned template is
`deploy/unraid/docker-compose.yml`, a standalone deployment variant, not an override.
No registry, GHCR publishing or CI image push; `docker login` is not needed.
`image: palabras:local` names the image built by `docker compose up -d --build` on this server.
Only Git, Docker and Docker Compose are needed on Unraid: the Dockerfile's multi-stage build
provides Node and the .NET SDK. Local dev and CI keep using the root compose/Dockerfile.

**Single source of truth:** `deploy/unraid/docker-compose.yml` in the source checkout.
`deploy/unraid-deploy.sh` copies it to the Docker share before each rebuild. Never edit the
Docker-share mirror; change and push the versioned template instead. The deployed `.env` is
server-owned and never overwritten by updates. The root compose is local-development only.

SQLite must not use the shfs/FUSE `/mnt/user` layer: locking semantics can cause errors or
corruption. `/mnt/user/repos` and `/mnt/user/docker` are fine for source/configuration; use the
direct cache-pool path for data. That path only stays populated if the Unraid mover never moves
it: the share holding it must use cache mode Only (or Prefer), not Yes (cache → array). On
2026-10-18 the `appdata` share was Yes, the mover moved the data to the array and the app started
on an empty `/data`; see `docs/08-upgrade-rollback.md` section C. Changing `APPDATA_PATH`
later does not move data; stop the app and migrate SQLite sidecars, keys and backups too.

### One-time private GitHub authentication

This project's repository is `https://github.com/michaelsieben/palabras`.
Use the dedicated `github-personal` SSH host alias on both local and Unraid machines.
On Unraid, the identity is `/root/.ssh/id_ed25519_michaelsieben`, comment `unraid-michaelsieben`.
Register only its public `.pub` half at https://github.com/settings/keys while signed in as
`michaelsieben`, type Authentication Key. It must not reuse another account's key.
This account authentication key is write-capable wherever `michaelsieben` has write permission;
server/root compromise can expose that access. Polling only fetches, but does not limit permissions.
The existing `Host github.com` identity and `backup.unraid.net` entry belong to other server uses
and must not be changed for this project. `/root/.ssh` persists via `/boot/config/ssh/root`.

```sshconfig
Host github-personal
    HostName github.com
    User git
    IdentityFile /root/.ssh/id_ed25519_michaelsieben
    IdentitiesOnly yes
    BatchMode yes
    StrictHostKeyChecking yes
```

```bash
ssh -T git@github-personal
```

Never copy the private key off the server, store it in Git/.env, or overwrite an existing identity.

Verify GitHub's host-key fingerprint against its published SSH fingerprints before accepting it.
GitHub's successful authentication greeting exits with code 1 because it offers no shell.
Require `Hi michaelsieben! You've successfully authenticated` before cloning; stop on another account
or a permission error. No PAT is needed.

**Rotate:** pause each affected repository's auto-deploy job, generate a new key at a different
server path, register its public half as an account authentication key, back up SSH config and
update `IdentityFile`. Verify the expected account greeting and private clone/fetch access before
resuming jobs. Delete only the replaced personal account key in GitHub Settings → SSH and GPG keys,
then remove its server key pair after validation. Never remove another account's shared server key.
**Revoke after compromise:** immediately delete the account key in GitHub; this disables all server
GitHub access using it, not just Palabras. Pause polling, investigate the host, then provision a new
identity on a trusted host. Existing running containers continue running; future fetches fail.

### Clone and first run, in order

```bash
cd /mnt/user/repos
git clone git@github-personal:michaelsieben/palabras.git palabras
cd palabras
cp .env.example .env
mkdir -p /mnt/user/docker/palabras
cp deploy/unraid/docker-compose.yml /mnt/user/docker/palabras/docker-compose.yml
# Edit checkout .env as below, then copy it beside the deployed compose.
```

Edit `/mnt/user/repos/palabras/.env` **before first run**:

```dotenv
APPDATA_PATH=/mnt/cache/appdata/palabras
APP_PORT=18480
APP_BASE_URL=https://palabras.siebentower.nl
```

`APP_BASE_URL` and password/VAPID placeholders are reserved for later milestones; M0 consumes no
credentials. Unraid compose rejects a blank data path. Protect `.env` when secrets are added.

M7 rollover gate: `M7__DayRolloverEnabled` (config key `M7:DayRolloverEnabled`) defaults to false. Only the
literal `true` (case-insensitive, trimmed) enables it; absent, empty, `false`, `1` or anything else keeps it off.
The schema migrates regardless, and while it is off the job writes no `missed` history. Leave it unset: enabling it
is a deliberate decision still gated on the outstanding pre-enablement items in `docs/07-learn-api.md` ("Rollover
and missed days") and `docs/08-upgrade-rollback.md` section D, which also holds the kill-switch procedure.

```bash
# First setup only: preserve an existing server .env.
test -f /mnt/user/docker/palabras/.env || cp /mnt/user/repos/palabras/.env /mnt/user/docker/palabras/.env
chmod 600 /mnt/user/repos/palabras/.env /mnt/user/docker/palabras/.env
cd /mnt/user/docker/palabras
mkdir -p /mnt/cache/appdata/palabras
chown 1654:1654 /mnt/cache/appdata/palabras
docker compose config --quiet
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker inspect palabras --format '{{json .NetworkSettings.Networks.sieben}}'
curl --fail https://palabras.siebentower.nl/healthz
```

Expected JSON includes `"status":"ok"` (also `version`). The non-root image uses UID/GID 1654;
bind mounts hide the image's directory ownership, so `chown` is needed for future writes.
Compose Manager may manage `/mnt/user/docker/palabras/docker-compose.yml` as project `palabras`;
point it at that folder/file and use its build action, not a registry pull. Do not create a second
project for the same container. Keep its working directory here so `.env` is read correctly.

### Automatic updates, pause and rollback

User Scripts plugin runs `palabras-auto-deploy` on custom cron `*/5 * * * *` (every five minutes).
Persistent wrapper: `/boot/config/plugins/user.scripts/scripts/palabras-auto-deploy/script`.
It executes the versioned `deploy/unraid-deploy.sh`; until clone exists, a bootstrap copy in the
Docker share logs the missing-checkout blocker. No inbound webhook or GitHub runner is used.

The script locks concurrent runs, fetches `origin/main`, accepts fast-forwards only, refuses
tracked local edits/detached HEAD, and skips rebuilding an unchanged healthy deployment. That skip
is not decided by `deployed.sha` alone: the script records the image id of each accepted deploy in
`deployed.image` and compares it with the image the container is actually running, so a container
left on an older build is rebuilt instead of being reported as current. With no image record for
the target SHA (a hand-written `deployed.sha`, or a deployment recorded before this check existed)
it falls back to comparing the container with `palabras:local` and logs that match as unverified.
The remaining known limitation is that the poll does not consult CI, so a commit whose CI is red is
built and deployed all the same (see `docs/08-upgrade-rollback.md` §E.3).
Before a rebuild the script tags the image the container is running as
`palabras:rollback-<image-id>` (idempotent, newest two kept, listed in `rollback-images`), so the
outgoing image no longer survives only as an untagged dangling image. An image that was already
untagged before the script ran, and `docker image prune -a`, are still not covered: record and tag
the running image by hand before any upgrade (`docs/08-upgrade-rollback.md` §E.4 step 1).
An unchanged run rejects a divergent Docker-share compose instead of silently accepting it;
restore the canonical file to resolve it. Never delete `deployed.sha` to force a rebuild: that
disables both data guards (docs/08-upgrade-rollback.md D.5).
After build/up the candidate must be running and serve `{"status":"ok"…}` on host `/healthz`
within a bounded wait (default 180 s; a crash loop fails at once). If it does not, the script
restores the image that ran before the rebuild with `docker compose up -d --no-build
--force-recreate`, verifies it serves again, logs `deploy FAILED, rolled back` (or `rollback FAILED`
if it cannot) and exits non-zero, retaining the last healthy SHA. The database is not restored. The
failed candidate is recorded in `failed.image` and never tagged as a rollback target. The next poll
retries the same target every five minutes until paused. Settings (`PALABRAS_HEALTH_*`, read from
the script's environment, not `.env`), log lines and limits: `docs/08-upgrade-rollback.md` §F.

Build identity on Unraid is wired in the repo. `deploy/unraid/docker-compose.yml` carries the `build.args`
entry `COMMIT_SHA: "${COMMIT_SHA:-unknown}"`, and `deploy/unraid-deploy.sh` exports `COMMIT_SHA` before
`docker compose up -d --build` from the checkout it builds (`git -C "$REPO" rev-parse HEAD`, read after the
fast-forward and immediately before the build, so it is the sha actually built and never the recorded
`deployed.sha`), logging `building sha=…` so a later deploy can be reconstructed. If the sha cannot be read the
build still runs and reports `unknown` instead of a guessed one. A poll-built image therefore reports its commit
in `/healthz` and Ajustes and carries it as the `org.opencontainers.image.revision` label. Not yet exercised end
to end: nothing here can run `docker compose`, so the interpolation was only simulated locally (script harness,
plus a real `docker build --build-arg COMMIT_SHA=<sha>` whose `/healthz` reported it), and the next poll rebuild on
the host is what proves it there. A container that predates this change still reports `commit: "unknown"`: use the
image id, `deployed.sha` and `deployed.image` to identify it.

Data guards (T-20261018-1547). The data directory is `APPDATA_PATH` read from the deployed `.env`.
Once `deployed.sha` exists, every unpaused poll first refuses to run (before fetching) when
`palabras.db` there is missing, a symlink, not a regular file or 0 bytes; it never writes
`deployed.sha` or recreates data. Before every rebuild it copies `palabras.db`, its `-wal`/`-shm`
and `keys/` to `/mnt/user/docker/palabras/backups/<UTC stamp>-<sha7>/` (newest 5 kept); a failed
copy aborts the deploy. After a rebuild, and on every unchanged healthy poll, it runs
`docker exec palabras dotnet /app/Palabras.dll user list`: zero accounts, fewer accounts than counted
before the rebuild, or a CLI that fails or prints no `ROLE` header exits non-zero without recording
the SHA, even when the container is healthy. Without `deployed.sha` (first deploy, or after deleting
it) the missing-database refusal is skipped and zero accounts is only a warning. The guards detect data loss but cannot prevent it; see
`docs/08-upgrade-rollback.md` ("C. Data directory emptied by the Unraid mover").

- Log: `/mnt/user/docker/palabras/deploy.log`, rotated at 1 MiB, three historical files.
- Last healthy deployed commit: `/mnt/user/docker/palabras/deployed.sha`.
- Image id of that deploy: `/mnt/user/docker/palabras/deployed.image` (`<image id> <sha>`); the next
  poll compares it with the running container.
- Preserved rollback tags: `/mnt/user/docker/palabras/rollback-images`, oldest first, newest two kept.
- Last candidate that failed the health gate: `/mnt/user/docker/palabras/failed.image` (`<image id> <sha>`);
  removed by the next successful deploy.
- Immediate manual poll: `bash /boot/config/plugins/user.scripts/scripts/palabras-auto-deploy/script`.
- Pause: `touch /mnt/user/docker/palabras/paused`; scheduled runs exit without fetching/building.
- Resume: `rm /mnt/user/docker/palabras/paused`, then run the wrapper manually.
- Disable: User Scripts → Palabras auto deploy → schedule Disabled → Apply. This persists over reboot.

Rollback restarts the image retained before the upgrade; it does not rebuild from source. Follow
`docs/08-upgrade-rollback.md` ("Rollback"): start the retained image ID with `docker compose up -d
--no-build`, and restore the pre-upgrade database set unless the old image passed the drill against
the migrated copy. A poll rebuild tags its own outgoing image (`palabras:rollback-<image-id>`)
before replacing it, but a hand-tagged `$ROLLBACK_TAG` is still needed whenever polling is paused
or the running image was already untagged. Do not check out an older SHA and run `docker compose up
-d --build`: floating base tags make that a different image, and the old code then runs against the
already migrated database.
Never run a down-migration. A deploy that adds a migration starts with that runbook's section A
(pre-migration backup, manual upgrade with polling paused), not with the poll. The next production deploy is a
large jump that carries five migrations; follow the ordered procedure in that runbook's section E.4.

Keep polling paused after a rollback: with `deployed.sha` unchanged a resumed poll does nothing, but
the next commit on `main` rebuilds and re-migrates. Resume as in that runbook's Rollback step 6.
To stop without the scheduler restarting the app:

```bash
touch /mnt/user/docker/palabras/paused
cd /mnt/user/docker/palabras
docker compose down
```

Never `down -v`; preserve data and future named volumes. Deleting the host data directory still
destroys data. Container recreation and ordinary `down` preserve the bind directory.

### Existing proxy host (no new proxy/DNS/TLS required)

Verified: `nginx-proxy-manager` (`jc21/nginx-proxy-manager:latest`) is on external user-defined
bridge `sieben`, subnet `172.18.0.0/16`, gateway `172.18.0.1`, proxy IP `172.18.0.37`.
Its previously discovered macvlan `br0` address is `192.168.68.2`. App joins `sieben` with alias and
container name `palabras` (name was free at discovery). Existing NPM proxy host:

- Domain: `palabras.siebentower.nl`; scheme: `https`.
- Forward hostname: `palabras`; forward port: `18480`.
- Existing certificate; Force SSL and HSTS on; Block Common Exploits on; Websockets off.

Upstream is `http://palabras:18480`. A 502 before the first successful `up -d` is expected.
If NPM cached a failed lookup, save/reload the existing proxy host after the app is running.
Exactly one published `127.0.0.1:18480:18480` port is debug/host-health only; plain HTTP is never
exposed to the LAN. Proxy traffic uses `sieben`, not
host loopback. `APP_PORT` changes both listen/published ports; update NPM's port if it changes.

**Previously discussed fallback upstream:** `http://192.168.68.66:18480` is not reachable with the
required loopback-only publishing. Do not broaden the binding or switch to this fallback as a
workaround; repair the `sieben` attachment/DNS instead. LAN HTTP exposure requires a separate,
explicit user-approved security change.

## Install as PWA

Android Chrome only (OnePlus 15 and OnePlus Tab 3). Manifest, icons, service worker, installability and offline support are deferred beyond this minimal M0 scaffold. No iOS work.

## Backup/restore

No database or backup implementation in M0. `/data` is reserved for SQLite, keys and backups. On Unraid, keep `/mnt/cache/appdata/palabras` (or the equivalent pool path) on a share whose cache mode is Only or Prefer (see "Layout and prerequisites") and include it in the appdata backup strategy. Automated backup and a tested restore procedure arrive in M8. Container recreation and `docker compose down` preserve the bind directory; do not delete it.

## Milestone implementation notes

- **M1 proxy/auth groundwork:** with the standalone `sieben` deployment, requests arrive from the proxy's `sieben` address (`172.18.0.37` on 2026-10-17), which can change when NPM is recreated. Configure the trusted subnet CIDR `172.18.0.0/16` in `KnownIPNetworks` (.NET 10; `KnownNetworks` is obsolete), not as a CIDR string in `KnownProxies`, which accepts individual IPs. This shared bridge trusts all attached containers; revisit isolation in M1. Real client IP comes from `X-Forwarded-For`, not the peer IP. Configure `UseForwardedHeaders` for XFF and XFP first in the pipeline, before auth and rate limiting, and verify post-forwarded `RemoteIpAddress`. Never enable blanket `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Cookies: `Secure=Always`, `__Host-` name, `Path=/`, no Domain, `SameSite=Lax`; proxy-to-app HTTP must not weaken cookie security. Rate-limit partitions use post-forwarded `RemoteIpAddress`. Replace wildcard `AllowedHosts` with `palabras.siebentower.nl;localhost;127.0.0.1`: container healthchecks and CI smoke use localhost/127.0.0.1, so excluding them answers 400 and breaks healthchecks. Alternatively send an explicit `Host: palabras.siebentower.nl` header from both checks. Add a healthcheck-host regression test in M1. No M0 runtime changes needed.
- **M2 local DB:** add an `appsettings.Development.json` connection-string override using `./data/palabras.db` before DB access is implemented. Extend Docker restore COPY inputs when additional projects or shared MSBuild/NuGet props appear.
- **Before M7 service worker:** serve root `/sw.js` and `index.html` with `Cache-Control: no-cache`; hashed `/assets/*` with `immutable, max-age=31536000` via `StaticFileOptions.OnPrepareResponse`.

## Feedback export

Not implemented. Reviewer feedback export and offline style-guide updates arrive later. No runtime LLM integration.

## Troubleshooting

API badge unavailable: check backend :5080 and Vite proxy; inspect browser network errors. Container unhealthy: `docker compose logs --tail 50 palabras`. Missing SPA in bare backend: use the Docker image or Vite dev server.
