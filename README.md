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

**User-run only; M0 has not been deployed.** The existing proxy is the container `nginx-proxy-manager` (`jc21/nginx-proxy-manager`), attached to macvlan `br0` at `192.168.68.2`; it publishes no host ports and handles DNS/TLS itself. Compose has no proxy and publishes neither 80 nor 443.

Because that proxy lives in another container on a macvlan network, it cannot reach the Unraid host's `127.0.0.1`, so M0 publishes the app on all host interfaces: `0.0.0.0:18480` → container `:8080`. Port 18480 was absent from the read-only container port scan on 2026-10-17 (:8080 is already used by sabnzbd); recheck before deployment, since the scan cannot detect every host process. Override with `APP_PORT` if it is ever taken. 18480 is LAN-only and must never be forwarded on the WAN side; all public traffic arrives through the proxy on 443.

Run on Unraid in a user-chosen project directory (requires Git and authenticated access to this private repo):

**Before the first deploy**, set `APPDATA_PATH=/mnt/user/appdata/palabras` in `.env`. Leaving it blank is for local dev only: Compose binds repo-local `./data` to `/data`. Changing `APPDATA_PATH` later does not migrate data; stop the app and move the DB file manually (and any SQLite sidecars, keys and backups) before restarting with the new path.

The runtime uses non-root `app` (UID/GID 1654 in the current .NET image). Prepare the Unraid directory with ownership matching that user before deployment; the Dockerfile's `/data` ownership is hidden by the bind mount. These are user-run commands, not operations performed by M0:

```bash
git clone git@github.com:sunmsieben/palabras.git
cd palabras
cp .env.example .env
# Edit .env: APPDATA_PATH=/mnt/user/appdata/palabras (BEFORE first deploy).
mkdir -p /mnt/user/appdata/palabras
chown 1654:1654 /mnt/user/appdata/palabras
docker compose config --quiet
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker compose ps
```

Compose bind-mounts `APPDATA_PATH` at `/data`; no named volume remains and no credentials are needed at M0. Local `data/` is excluded from Git and Docker build context. For local Linux containers, also ensure the bind directory is writable by UID/GID 1654 before M2 introduces writes. Never use `docker compose down -v` once data matters: it no longer deletes this bind-mounted data, but can delete other volumes added later. Deleting the host directory (for example with `rm -rf`) still destroys the data.

Loopback binding was replaced by the all-interfaces binding above, so the proxy needs no shared Docker network and no `docker network connect`. Leave it as is until a deployment proves otherwise.

### Configure the proxy (nginx-proxy-manager)

Proxy Host, in NPM → Hosts → Proxy Hosts → Add Proxy Host:

- Domain Names: `palabras.siebentower.nl`
- Scheme: `http`
- Forward Hostname / IP: `192.168.68.66` (Unraid LAN IP)
- Forward Port: `18480`
- Block Common Exploits: on
- Websockets Support: off (no WS/SignalR; push is delivered by FCM, not this origin)
- SSL: the existing Let's Encrypt certificate for `palabras.siebentower.nl`, Force SSL on, HTTP/2 on, HSTS on

The proxy can be configured now, but it returns 502 until the container is deployed. Deploying to Unraid is a user-run step, not an agent step.

Alternative upstream `http://palabras:8080` (container name, no host port) applies only if the app later joins the proxy's Docker network (for example `sieben`, `172.18.0.0/16`); persist that in a Compose override before recreating the container.

The proxy sits on macvlan `br0`, so reaching the host LAN IP is the expected path. If it returns 502, check from inside the proxy container:

```bash
docker exec nginx-proxy-manager curl -H "Host: palabras.siebentower.nl" http://192.168.68.66:18480/healthz
```

## Install as PWA

Android Chrome only (OnePlus 15 and OnePlus Tab 3). Manifest, icons, service worker, installability and offline support are deferred beyond this minimal M0 scaffold. No iOS work.

## Backup/restore

No database or backup implementation in M0. `/data` is reserved for SQLite, keys and backups. On Unraid, include `/mnt/user/appdata/palabras` in the appdata backup strategy. Automated backup and a tested restore procedure arrive in M8. Container recreation and `docker compose down` preserve the bind directory; do not delete it.

## Milestone implementation notes

- **M1 proxy/auth groundwork:** confirm the source IP seen through the published LAN port before setting `FORWARDED_KNOWN_NETWORK`. Configure `UseForwardedHeaders` for XFF and XFP with explicit `KnownNetworks`/`KnownProxies`, first in the pipeline, before auth and rate limiting. Never enable blanket `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Use cookies with `Secure=Always`, `__Host-` name, `Path=/`, no Domain and `SameSite=Lax`; proxy-to-app HTTP must not weaken cookie security. Rate-limit partitions must use post-forwarded `RemoteIpAddress`. Replace wildcard `AllowedHosts` with the real host. No M0 runtime changes needed.
- **M2 local DB:** add an `appsettings.Development.json` connection-string override using `./data/palabras.db` before DB access is implemented. Extend Docker restore COPY inputs when additional projects or shared MSBuild/NuGet props appear.
- **Before M7 service worker:** serve root `/sw.js` and `index.html` with `Cache-Control: no-cache`; hashed `/assets/*` with `immutable, max-age=31536000` via `StaticFileOptions.OnPrepareResponse`.

## Feedback export

Not implemented. Reviewer feedback export and offline style-guide updates arrive later. No runtime LLM integration.

## Troubleshooting

API badge unavailable: check backend :5080 and Vite proxy; inspect browser network errors. Container unhealthy: `docker compose logs --tail 50 palabras`. Missing SPA in bare backend: use the Docker image or Vite dev server.
