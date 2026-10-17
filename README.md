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

**User-run only; M0 has not been deployed.** Existing nginx proxy handles DNS/TLS. Compose has no proxy and publishes neither 80 nor 443. Host :18480 was absent from the read-only container port scan on 2026-10-17 (:8080 is already used by sabnzbd). Recheck before deployment; the scan cannot detect every host process.

Run on Unraid in a user-chosen project directory (requires Git and authenticated access to this private repo):

```bash
git clone git@github.com:sunmsieben/palabras.git
cd palabras
cp .env.example .env
docker compose config --quiet
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker compose ps
```

Compose creates the named volume `palabras-appdata`, mounted at `/data`; no credentials are needed at M0. `APPDATA` is a reserved placeholder and does not change this named volume. Never use `docker compose down -v` when data matters.

Loopback binding is deliberate. A proxy in another container cannot route to the Unraid host's `127.0.0.1:18480`. Before later public deployment, identify the proxy's actual network and choose an existing **bridge** network shared by both containers; do not assume `br0` (macvlan) supports this. User may attach the running app to that verified network:

```bash
# Replace <verified-proxy-bridge-network> after inspecting the existing proxy.
docker network connect --alias palabras <verified-proxy-bridge-network> "$(docker compose ps -q palabras)"
```

Then configure the existing proxy upstream as `http://palabras:8080`, and wire DNS/TLS yourself. Persist shared network membership in a later Compose override before recreating the container. This step is not automated or performed by M0; unauthenticated M0 must remain private.

## Install as PWA

Android Chrome only (OnePlus 15 and OnePlus Tab 3). Manifest, icons, service worker, installability and offline support are deferred beyond this minimal M0 scaffold. No iOS work.

## Backup/restore

No database or backup implementation in M0. `/data` is reserved for SQLite, keys and backups. Automated backup and a tested restore procedure arrive in M8; include named volumes in your Unraid backup strategy.

## Feedback export

Not implemented. Reviewer feedback export and offline style-guide updates arrive later. No runtime LLM integration.

## Troubleshooting

API badge unavailable: check backend :5080 and Vite proxy; inspect browser network errors. Container unhealthy: `docker compose logs --tail 50 palabras`. Missing SPA in bare backend: use the Docker image or Vite dev server.
