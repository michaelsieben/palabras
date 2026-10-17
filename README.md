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

Expected health JSON: `{"status":"ok","version":"0.0.1.0"}`. `/api/ping` returns the same shape. Unknown API paths return 404; other extensionless paths use SPA fallback.

## Content authoring

`content/words/`, `content/schema/` and `tools/` are placeholders. Schema, style guide, importer and content lint arrive in M2. No content generation in M0.

## Deploy on Unraid

**User-run only; M0 has not been deployed. Do not expose this unauthenticated scaffold publicly.**
Run these commands yourself as root on Unraid. Agents only performed read-only discovery.

### Layout and prerequisites

Verified 2026-10-17: `/mnt/user/repos` exists (shfs user-share path; `/mnt/cache/repos` does not).
It contains separate Git checkouts `SoftwareFactory`, `VaporEngine` and `budgeteer`.
`/mnt/user/docker` and `/mnt/cache/appdata` also exist; `palabras` is not yet deployed.

- Source: `/mnt/user/repos/palabras`, cloned from private GitHub repo.
- Compose project: `/mnt/user/docker/palablras/docker-compose.yml` and adjacent `.env` (historical spelling).
- Persistent data: `/mnt/cache/appdata/palabras`, bind-mounted to `/data`.

The Docker share holds the standalone compose, as requested; it builds from the separate repos
checkout through an absolute `build.context`. The versioned template is
`deploy/unraid/docker-compose.yml`, a standalone deployment variant, not an override.
No registry, GHCR publishing or CI image push; `docker login` is not needed.
`image: palabras:local` names the image built by `docker compose up -d --build` on this server.
Only Git, Docker and Docker Compose are needed on Unraid: the Dockerfile's multi-stage build
provides Node and the .NET SDK. Local dev and CI keep using the root compose/Dockerfile.

**Folder verification warning (2026-10-17):** fresh read-only checks found neither
`/mnt/user/docker/palablras/docker-compose.yml` nor the corrected `palabras` path. The earlier
discovery reported `palablras`; confirm the actual app folder before proceeding. Reuse whichever
spelling already exists rather than creating a second project. Commands below preserve the earlier
spelling; if no app folder exists, only the user creates it after checking. Container name and
`sieben` alias must remain `palabras` regardless of folder spelling.

The typo is cosmetic. If the user later renames the existing folder, with the app stopped and no
destination folder present: `mv /mnt/user/docker/palablras /mnt/user/docker/palabras`.
Then use the corrected path below; `name: palabras` keeps the Compose project stable.

SQLite must not use the shfs/FUSE `/mnt/user` layer: locking semantics can cause errors or
corruption. `/mnt/user/repos` and `/mnt/user/docker` are fine for source/configuration; use the
direct cache-pool path for data, and keep the appdata share on that pool. Changing `APPDATA_PATH`
later does not move data; stop the app and migrate SQLite sidecars, keys and backups too.

### One-time private GitHub authentication

Recommended: a dedicated **read-only deploy key for this repository only**, not an account-wide key.
On the server:

```bash
mkdir -p /root/.ssh
chmod 700 /root/.ssh
ssh-keygen -t ed25519 -C "palabras-unraid-readonly" -f /root/.ssh/id_ed25519_palabras -N ''
chmod 600 /root/.ssh/id_ed25519_palabras
```

Do not overwrite an existing key. Copy the contents of `/root/.ssh/id_ed25519_palabras.pub` to
GitHub → `sunmsieben/palabras` → Settings → Deploy keys → Add deploy key; leave **Allow write access
unchecked**. This public key belongs in GitHub's deploy-key settings, not Unraid's `authorized_keys`.
Never share the private key or store it in the repo or `.env`.

```bash
export GIT_SSH_COMMAND='ssh -i /root/.ssh/id_ed25519_palabras -o IdentitiesOnly=yes'
ssh -i /root/.ssh/id_ed25519_palabras -o IdentitiesOnly=yes -T git@github.com
```

Verify GitHub's host-key fingerprint against its published SSH fingerprints before accepting it.
GitHub's successful authentication greeting exits with code 1 because it offers no shell.
Repeat the `export` in each new shell before pull, or configure a dedicated SSH host alias using
this identity. Persist `/root/.ssh` securely across Unraid reboots using your existing SSH setup;
root's home may be rebuilt at boot.

Alternative: a fine-grained PAT restricted to `sunmsieben/palabras`, **Contents: Read-only**.
Use `https://github.com/sunmsieben/palabras.git` for clone, enter your GitHub username and PAT at
Git's password prompt, and authenticate again on pull as needed. Do not embed PAT in clone URL,
shell history, Git config or `.env`.

### Clone and first run, in order

```bash
cd /mnt/user/repos
git clone git@github.com:sunmsieben/palabras.git palabras
cd palabras
cp .env.example .env
# Confirm/reuse the existing app folder before this user-run mkdir.
mkdir -p /mnt/user/docker/palablras
cp deploy/unraid/docker-compose.yml /mnt/user/docker/palablras/docker-compose.yml
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

```bash
cp /mnt/user/repos/palabras/.env /mnt/user/docker/palablras/.env
chmod 600 /mnt/user/repos/palabras/.env /mnt/user/docker/palablras/.env
cd /mnt/user/docker/palablras
mkdir -p /mnt/cache/appdata/palabras
chown 1654:1654 /mnt/cache/appdata/palabras
docker compose config
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker inspect palabras --format '{{json .NetworkSettings.Networks.sieben}}'
curl --fail https://palabras.siebentower.nl/healthz
```

Expected JSON includes `"status":"ok"` (also `version`). The non-root image uses UID/GID 1654;
bind mounts hide the image's directory ownership, so `chown` is needed for future writes.
Compose Manager may manage `/mnt/user/docker/palablras/docker-compose.yml` as project `palabras`;
point it at that folder/file and use its build action, not a registry pull. Do not create a second
project for the same container. Keep its working directory here so `.env` is read correctly.

### Update, rollback and stop

Update (repeat the Git SSH export above in a new shell):

```bash
cd /mnt/user/repos/palabras
git rev-parse HEAD   # record previous SHA for rollback
git pull --ff-only && (cd /mnt/user/docker/palablras && docker compose up -d --build)
# If the deployment template changed, refresh it and rebuild again:
cp deploy/unraid/docker-compose.yml /mnt/user/docker/palablras/docker-compose.yml
cd /mnt/user/docker/palablras
docker compose config
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
```

Preserve the deployed `.env`; do not overwrite it during updates. Reapply any deliberate local
compose edits after copying the template. For rollback, replace `<previous-sha>` with the recorded
commit, then rebuild (future DB migrations may require a backup restore as well):

```bash
cd /mnt/user/repos/palabras
git checkout --detach <previous-sha>
# Use the retained deployed compose if this SHA predates the template.
cp deploy/unraid/docker-compose.yml /mnt/user/docker/palablras/docker-compose.yml
cd /mnt/user/docker/palablras
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
```

Return to `main` before the next normal pull. To stop:

```bash
cd /mnt/user/docker/palablras
docker compose down
```

Never `down -v`; preserve data and future named volumes. Deleting the host data directory still
destroys data. Container recreation and ordinary `down` preserve the bind directory.

### Existing proxy host (no new proxy/DNS/TLS required)

Verified: `nginx-proxy-manager` (`jc21/nginx-proxy-manager:latest`) is on external user-defined
bridge `sieben`, subnet `172.18.0.0/16`, gateway `172.18.0.1`, proxy IP `172.18.0.37`.
Its previously discovered macvlan `br0` address is `192.168.68.2`. App joins `sieben` with alias and
container name `palabras` (name was free at discovery). Existing NPM proxy host:

- Domain: `palabras.siebentower.nl`; scheme: `http`.
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

No database or backup implementation in M0. `/data` is reserved for SQLite, keys and backups. On Unraid, keep `/mnt/cache/appdata/palabras` (or the equivalent pool path) inside the `appdata` share and include that share in the appdata backup strategy. Automated backup and a tested restore procedure arrive in M8. Container recreation and `docker compose down` preserve the bind directory; do not delete it.

## Milestone implementation notes

- **M1 proxy/auth groundwork:** with the standalone `sieben` deployment, requests arrive from the proxy's `sieben` address (`172.18.0.37` on 2026-10-17), which can change when NPM is recreated. Configure the trusted subnet CIDR `172.18.0.0/16` in `KnownIPNetworks` (.NET 10; `KnownNetworks` is obsolete), not as a CIDR string in `KnownProxies`, which accepts individual IPs. This shared bridge trusts all attached containers; revisit isolation in M1. Real client IP comes from `X-Forwarded-For`, not the peer IP. Configure `UseForwardedHeaders` for XFF and XFP first in the pipeline, before auth and rate limiting, and verify post-forwarded `RemoteIpAddress`. Never enable blanket `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Cookies: `Secure=Always`, `__Host-` name, `Path=/`, no Domain, `SameSite=Lax`; proxy-to-app HTTP must not weaken cookie security. Rate-limit partitions use post-forwarded `RemoteIpAddress`. Replace wildcard `AllowedHosts` with `palabras.siebentower.nl;localhost;127.0.0.1`: container healthchecks and CI smoke use localhost/127.0.0.1, so excluding them answers 400 and breaks healthchecks. Alternatively send an explicit `Host: palabras.siebentower.nl` header from both checks. Add a healthcheck-host regression test in M1. No M0 runtime changes needed.
- **M2 local DB:** add an `appsettings.Development.json` connection-string override using `./data/palabras.db` before DB access is implemented. Extend Docker restore COPY inputs when additional projects or shared MSBuild/NuGet props appear.
- **Before M7 service worker:** serve root `/sw.js` and `index.html` with `Cache-Control: no-cache`; hashed `/assets/*` with `immutable, max-age=31536000` via `StaticFileOptions.OnPrepareResponse`.

## Feedback export

Not implemented. Reviewer feedback export and offline style-guide updates arrive later. No runtime LLM integration.

## Troubleshooting

API badge unavailable: check backend :5080 and Vite proxy; inspect browser network errors. Container unhealthy: `docker compose logs --tail 50 palabras`. Missing SPA in bare backend: use the Docker image or Vite dev server.
