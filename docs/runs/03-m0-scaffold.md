# T-20261017-1516 / Step 03 — Palabras M0 scaffold

## Result

STATUS: ok

- Local repo: `D:\Visual Studio Repo\Personal\Palabras`.
- Private personal repo: https://github.com/sunmsieben/palabras (`visibility: PRIVATE`).
- Branch: `main`, tracking `origin/main`; clean tracked working tree.
- Commit: `561de60287c44310412c8d739daaba1d8621d93b` (`chore: scaffold Palabras M0`).
- GitHub CI: **success**, https://github.com/sunmsieben/palabras/actions/runs/39037127620.
- No Unraid deployment, container mutation, proxy edits or DNS/TLS changes performed.

## Grounding and selected rules

Read design `01-spanish-pwa-design.md` first, then implementation contract and language modules `dotnet.md`, `typescript-javascript.md`. No pre-existing Palabras files or local instructions existed; created scoped `AGENTS.md`. Amendment was not read or awaited. Fixed M0 prompt overrides broader original design deployment/features.

## Environment verification

Read-only checks ran before project creation. Exact output preserved in `03-environment.log`; exit-coded repeat in `03-environment-exits.log`.

- `gh auth status`: active account **sunmsieben**, keyring, SSH Git transport, token scopes `admin:public_key`, `gist`, `read:org`, `repo`. Status exits 1 because an **inactive** `michaelsieben` default token is invalid. No account switch or auth changes made. `gh api user` confirms active `{"login":"sunmsieben","type":"User"}`; repo create/push/view subsequently all exit 0. Inactive stale token is a non-blocking environment warning, not an active-account authorization failure.
- `dotnet --list-sdks`: **8.0.425**, **10.0.401**, both under `C:\Program Files\dotnet\sdk`.
- Actual local build SDK, `dotnet --version`: **10.0.401**. Pinned in `global.json` with `latestPatch`, no prerelease. CI uses 10.0.401; Docker uses the .NET 10 SDK/runtime image family. No SDK installed.
- `node -v`: **v24.21.0**.
- `npm -v`: **12.2.0**.
- `docker --version`: **Docker version 29.8.2, build 7fc2dff**.
- `docker compose version`: **Docker Compose version v5.5.1**.
- Docker engine: Linux, server **29.8.2**.
- All version checks exit 0; only aggregate `gh auth status` exits 1 for the inactive token above.

## Unraid read-only scan and port choice

Exact commands printed before execution; both exit 0:

```bash
ssh unraid-docker 'docker ps --format "{{.Names}}\t{{.Ports}}"'
ssh unraid-docker 'docker network ls'
```

Full outputs: `03-unraid-ports.log`, `03-unraid-networks.log`.

Published TCP host ports shown: **1883, 2283, 3030, 3040, 4096, 5000, 5055, 6379, 6767, 6881, 7171, 7878, 7900, 7912, 8000, 8048, 8080, 8090, 8100, 8112, 8123, 8181, 8265–8266, 8423, 8989, 9001, 9696, 10200, 10300, 21115–21117, 25600**. Published UDP: **6881, 21116**. Container-only exposed ports are not counted as host bindings. Host-network containers and non-Docker listeners may use other ports; this scan is not a full host socket inventory.

Likely existing proxy: **nginx-proxy-manager**; exposes container ports 80–81/443 but shows no published host binding. Its exact image/network/IP were not inspected. Networks found: `br0` (macvlan), `bridge`, `filament-tracking_filament-net`, `host`, `none`, `rustdesk_default`, `sieben`, `speedtest-tracker_default`, `sunweb-monitor_default`.

Chosen host port: **18480**. Absent from scan; avoids known :8080 collision with `sabnzbd`. Local Docker successfully bound `127.0.0.1:18480`, too. Compose uses exactly `127.0.0.1:18480:8080`.

## Scope and behavior

- Minimal .NET 10 API serves built frontend from `wwwroot`, with extensionless SPA fallback.
- `/healthz` and `/api/ping`: JSON `{"status":"ok","version":"0.0.1.0"}`. Unknown API paths return 404 instead of HTML.
- React 19.3.1 + strict TypeScript 6.0.3 + Vite 8.3.4: one Spanish placeholder screen, accessible live API status badge, abortable request cleanup. Vite `/api` and `/healthz` proxy to localhost:5080.
- One intentionally trivial xUnit passing test. It proves test runner setup, not API regression coverage. Real container requests separately verified both endpoints and fallback behavior.
- Docker: Node build → .NET publish → ASP.NET runtime, curl healthcheck, `USER app`, EXPOSE 8080; writable `/data` directory prepared. No root runtime.
- Compose: one locally built `palabras` service, named appdata volume mounted at `/data`, `env_file: .env`, restart policy and healthcheck. No proxy, 80/443 publishing, host networking or privileged mode.
- Env file contains the full original design surface reserved through M8 plus runtime/connection-string placeholders. Future auth, push, backup, forwarded-network and deployment settings are **not implemented or consumed** in M0. Password/key values are blank; no secrets committed.
- CI: one job; .NET restore/build/test, npm ci, eslint, TypeScript/Vite build, Docker build. No deployment or registry push.

Smaller choices where original design exceeds fixed M0 scope: no Domain/Infrastructure projects, Tailwind, query library, compose override, content schema/style guide/importer, router, manifest/icons/service worker/installability/offline implementation. Empty feature/API/SW/offline/type directories preserve frontend layout without feature code. Named volume and loopback port deliberately replace original design's bind mount/external proxy network. `/healthz` checks process availability only; no DB or writable-data probe until DB lands. No auth, EF packages/migrations, SRS, VAPID/push or LLM council added.

## Changed files

39 newly committed files, all under `Palabras/`:

```text
.dockerignore
.env.example
.gitattributes
.github/workflows/ci.yml
.gitignore
AGENTS.md
Backend/Palabras.slnx
Backend/Palabras/Palabras.csproj
Backend/Palabras/Program.cs
Backend/Palabras/appsettings.json
Backend/Palabras/wwwroot/.gitkeep
Backend/Palabras.Tests/Palabras.Tests.csproj
Backend/Palabras.Tests/ScaffoldTests.cs
Dockerfile
Frontend/package.json
Frontend/package-lock.json
Frontend/eslint.config.js
Frontend/index.html
Frontend/tsconfig.json
Frontend/tsconfig.app.json
Frontend/tsconfig.node.json
Frontend/vite.config.ts
Frontend/src/App.tsx
Frontend/src/main.tsx
Frontend/src/style.css
Frontend/src/api/.gitkeep
Frontend/src/components/.gitkeep
Frontend/src/features/admin/.gitkeep
Frontend/src/features/learn/.gitkeep
Frontend/src/features/review/.gitkeep
Frontend/src/sw/.gitkeep
Frontend/src/offline/.gitkeep
Frontend/src/types/.gitkeep
README.md
content/schema/.gitkeep
content/words/.gitkeep
docker-compose.yml
global.json
tools/.gitkeep
```

Additional ignored local `.env` copied from placeholders for Compose validation, no secrets. This report and `03-*.log` evidence live in caller workspace `.opencode/runs/T-20261017-1516/`, outside the new repo; not pushed.

## Verification: actual commands/results

- `dotnet build`, `dotnet test`, cwd `Palabras/Backend`: exit 0; **0 warnings, 0 errors; 1 passed, 0 failed, 0 skipped**. Also restore/build/test Release variants exactly matching CI: exit 0. `03-dotnet-final.log`; earlier successful root-solution run preserved in `03-dotnet.log` before moving solution to design's Backend layout.
- `npm install`, `npm ci`, `npm run lint`, `npm run build`, cwd `Palabras/Frontend`: final all exit 0, 160 packages audited, 0 vulnerabilities; `tsc -b && vite build`, 16 modules transformed, production assets generated. `03-frontend-retry.log`.
- Initial npm install failed: `peer typescript@">=4.8.4 <6.1.0" from typescript-eslint@8.71.2`; selected stable compatible TS **6.0.3** instead of latest TS7, no force/peer bypass. Initial failure preserved in `03-frontend.log`.
- Initial `docker build -t palabras:m0 .` failed due local tool environment: `exec: "docker-credential-desktop": executable file not found in %PATH%`. Plain PowerShell repeat also failed. Installed helper exists; prepending its installed bin directory to **process-only** PATH resolves lookup. No Docker config/auth changes or helper disabling. Both failures preserved in `03-docker-build.log`, `03-docker-build-retry.log`.
- Successful equivalent local build, cwd `Palabras`, exit 0, image `palabras:m0`, evidence `03-docker-build-path.log`:

```bash
pwsh -NoProfile -Command '$env:PATH = "C:\Users\faite\AppData\Local\Programs\DockerDesktop\resources\bin;" + $env:PATH; docker build -t palabras:m0 .; exit $LASTEXITCODE'
```

- `docker run --rm -d --name palabras-m0-smoke -p 127.0.0.1:18480:8080 palabras:m0`: exit 0. `/healthz` and `/api/ping` returned exact JSON above; `/placeholder-route` returned built SPA; `/api/missing` returned 404. Startup requires waiting for API listener: initial curl got `curl: (52) Empty reply from server`; repeat with `--retry-all-errors` succeeds, exit 0. Failures and corrected requests preserved in `03-container-smoke.log`, `03-container-smoke-retry.log`.
- Container configured user `app`, Docker health state **healthy**. `cp .env.example .env && docker compose config --quiet`: exit 0. Smoke containers stopped/auto-removed; no container left running by M0. `03-compose-cleanup.log`.
- Browser checks against built container: mobile **390×844** and desktop/landscape **1440×900**, title Palabras, status text **API conectada**, no page errors reported, no vertical scrolling. Actual OnePlus devices not tested.
- `git add .`, `git diff --cached --check`, `git commit`: exit 0. `gh repo create palabras --private --source=. --remote=origin --push`: exit 0. `gh repo view palabras --json visibility,url`: exit 0, **PRIVATE**. `03-github.log`.
- `gh run watch 39037127620 --repo sunmsieben/palabras --exit-status`: exit 0; completed **success**. Watch evidence `03-ci.log`; full runner output `03-ci-job.log`.

## User-run deployment commands (not performed)

On Unraid, in user-chosen project directory, with Git/private-repo SSH access available:

```bash
git clone git@github.com:sunmsieben/palabras.git
cd palabras
cp .env.example .env
docker compose config --quiet
docker compose up -d --build
curl --fail http://127.0.0.1:18480/healthz
docker compose ps
```

Leave unauthenticated M0 private. The existing **containerized** nginx proxy cannot reach host loopback directly. Before later public routing, user verifies proxy networking and selects a bridge network shared with the proxy; neither `sieben` nor any other network is assumed correct from names alone. A user-run temporary attachment, after replacing the network placeholder:

```bash
docker network connect --alias palabras <verified-proxy-bridge-network> "$(docker compose ps -q palabras)"
```

Proxy upstream then uses `http://palabras:8080`; user owns DNS/TLS. Persist network membership in a later Compose override before recreating containers. No nginx changes/commands were executed or invented. Named volume requires its own backup strategy; `APPDATA` placeholder does not bind-mount it. Never run `docker compose down -v` once data matters.

## Risks / next handover

No M0 blockers remain. Active personal GitHub identity worked; inactive stale token and local helper PATH remain environment warnings. CI confirms Docker build works without local workaround on Linux.

NEXT: `dotnet-reviewer` — read-only M0 backend/API/Docker/CI review; `general` for frontend/config read-only review as needed. Orchestrator owns amendment reconciliation, public proxy-network design, auth sequencing and eventual user-approved deployment. Do not claim public HTTPS, installability, content or auth complete.
