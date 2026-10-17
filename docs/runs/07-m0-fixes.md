# T-20261017-1516 / Step 07 — M0 fixes

STATUS: ok

## Scope and grounding

Repo: `D:\Visual Studio Repo\Personal\Palabras`. Read caller's review, scaffold and proxy-bind reports from the parent workspace `.opencode/runs/T-20261017-1516/`; that is also this report's location, consistent with prior steps. Reports/logs are outside the Git repo and are not committed.

Read implementation contract, `dotnet.md`, `typescript-javascript.md`, repo `AGENTS.md`, current CI/Docker/Compose/env files, project manifests, frontend scripts/lockfile/strict config and existing API tests. No runtime feature expansion, architecture change, auth, EF Core, SRS, push/VAPID or council tooling. No files under `Backend/Palabras.Tests/` changed; existing seven tests remain intact.

Working tree already carried Step 04 proxy-binding edits in `.env.example`, `AGENTS.md`, `README.md`, `docker-compose.yml`. Preserved them, including exact `0.0.0.0:${APP_PORT:-18480}:8080`; include them in the requested local commit rather than reverting or isolating away the reviewed deployment state.

## Changes

- `docker-compose.yml`: use `${APPDATA_PATH:-./data}:/data`; remove unused named-volume declaration. Keep LAN publishing and existing healthcheck.
- `.env.example`: document blank/local `./data` and Unraid `/mnt/user/appdata/palabras`; warn to set before first deploy; remove dead APPDATA/PROXY_NETWORK/PALABRAS_TAG placeholders, retain wired APP_PORT.
- `.gitignore`, `.dockerignore`: exclude root data/appdata directories without ignoring nested source directories.
- `.github/workflows/ci.yml`: same job builds/runs image after existing checks, publishes loopback 18080, retries startup health request, asserts status JSON, API 404 and SPA 200. Separate `if: always()` step logs and force-removes container; guarded absence handles failures before container creation. Restrict pushes to main; PR checks remain.
- `README.md`: bind persistence, pre-deploy Unraid env setting, manual relocation warning, UID/GID 1654 preparation, accurate deletion/backup guidance and deferred M1/M2/M7 review groundwork.
- `AGENTS.md`: only retained pre-existing Step 04 LAN/proxy clarification.

## Review-item disposition

1. **SHOULD-FIX — fixed previously, verified here.** Tautology already replaced by `M0ApiTests.cs` and WebApplicationFactory coverage in commit `7cb2351`. All seven pass. No further test changes necessary.
2. **SHOULD-FIX — fixed.** Unraid-visible bind path, local fallback, ignores, ownership instructions and migration/backup warnings supplied. No named volume remains.
3. **SHOULD-FIX — fixed as M1 brief only, as requested by review.** README records first-in-pipeline XFF/XFP handling with explicit proxy/network trust after observing actual peer IP, no blanket forwarded-headers env switch, secure `__Host-` cookie constraints and post-forwarded client-IP rate-limit partitions. No M0 middleware/auth added.
4. **NIT — runtime change deliberately deferred; milestone note added.** Root service-worker scope and no-cache index/SW versus immutable hashed assets recorded for before M7. No SW exists in M0; changing cache behavior now is unnecessary scope.
5. **NIT — runtime change deliberately deferred; milestone note added.** Real AllowedHosts belongs to M1 proxy/auth configuration; wildcard remains suitable for current local scaffold/smoke tests.
6. **NIT — runtime change deliberately deferred; milestone note added.** Development SQLite path override recorded for M2, when DB is actually consumed. M0 has no DB access, so no inert config file added.
7. **NIT — fixed.** Image traffic smoke and always-run logs/cleanup added; push trigger restricted to main.
8. **NIT — deliberately skipped current SDK pin/copy change.** Review explicitly accepts floating `sdk:10.0`; choosing stricter container SDK reproducibility policy is not needed to fix M0. Docker restore still has its complete current single-project input. README records extending COPY when shared props/new projects arrive at M2; nonexistent future files cannot be copied now.
9. **NIT — fixed.** Root-anchor `/data/`, `/appdata/` in Git ignore (and Docker ignore).
10. **NIT — fixed.** Wire APPDATA_PATH and remove dead APPDATA, PROXY_NETWORK, PALABRAS_TAG. APP_PORT remains consumed by Compose.

## Actual verification

All required checks ran locally; all final commands exited 0. Full command output retained in `07-*.log` beside this report.

- Repo root: `dotnet build Backend/Palabras.slnx`: **0 warnings, 0 errors**, exit 0. `dotnet test Backend/Palabras.slnx`: **7 passed, 0 failed, 0 skipped**, exit 0. Log: `07-dotnet.log`.
- `Palabras/Frontend`: `npm ci && npm run lint && npm run build`: exit 0; audit **0 vulnerabilities**, ESLint clean, TypeScript/Vite production build successful (16 modules). Log: `07-frontend.log`.
- Repo root: `docker compose config --quiet`, `git diff --check`: exit 0. Existing ignored `.env` used; no secrets or environment file changes. Log: `07-compose.log`.
- Repo root: `docker compose config --format json` projected to volume/port fields only, with assertions: default resolves to repo-local `Palabras/data`, type bind, target `/data`, no named-volume map; host IP `0.0.0.0`, port 18480 → 8080. Exit 0. Log: `07-compose-bind.log`.
- Repo root: `docker build -t palabras:local .`: exit 0. Used previously established **process-only** PATH prepend for installed Docker Desktop credential helper; no auth/config changes. Log: `07-docker-build.log`.
- Repo root: `docker run -d --name palabras-m0-fixes-smoke -p 127.0.0.1:18080:8080 palabras:local`, followed by retrying `curl --retry-all-errors --retry 30 --retry-delay 1 --fail http://127.0.0.1:18080/healthz`: **200**, exact `{"status":"ok","version":"0.0.1.0"}`; required body assertion passes. Initial startup `curl: (52) Empty reply from server` retained in log; built-in retry succeeds after one second.
- Same container: `/api/nope` **404**, `/anything` **200**; extra previously unverified bare `/api` **404**. All assertions pass. Logs dumped and container force-removed via EXIT trap; aggregate smoke exit 0. `docker run --rm --entrypoint id palabras:local app` confirms **UID/GID 1654**. Log: `07-container-smoke.log`.

## Risks and handover

- Unraid deploy, directory creation/chown, proxy edits, DB migration and backup were **not performed**; deployment stays user-run. M0 health proves serving traffic, not future DB-directory writability or backup recovery.
- Local smoke exercises the same traffic assertions as new CI, but GitHub workflow execution awaits orchestrator's later push; no remote CI success claimed.
- Local commit **`8b661fc8ebaa0d23c1e6fd6545ece999e0737a73`**, subject `T-20261017-1516: fix M0 storage and CI smoke`. Explicit seven-file stage, staged diff check and commit exit 0; tracked working tree clean. Log: `07-git.log`. **No push performed.** Logs/report stay in parent workspace, outside repo.
- NEXT: `dotnet-reviewer` — first review Step 07 diff, CI smoke cleanup, bind-mount documentation and preserved Step 04 proxy binding; then confirm seven existing tests remain untouched. Orchestrator owns push and future milestone design.
