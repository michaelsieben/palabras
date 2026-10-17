# T-20261017-1516 / 05 — M0 review (Palabras)

Verdict: **changes-requested (no blockers)**. 0 BLOCKER, 3 SHOULD-FIX, 7 NIT.
Reviewed working tree (shell denied, so `git status`/committed-state diff NOT run; docker-compose.yml/README.md may carry the concurrent port-binding edit — overlap noted, not flagged).
Paths relative to `Palabras/`.

## Findings

1. SHOULD-FIX `Backend/Palabras.Tests/ScaffoldTests.cs:10` — `Assert.Equal(4, 2 + 2)` is a tautology; never touches app code despite ProjectReference (`Palabras.Tests.csproj:18`). `dotnet test` does run it (log: Passed 1/1), but it guards nothing. Fix: add `Microsoft.AspNetCore.Mvc.Testing`, `public partial class Program;` at end of `Program.cs`, and `WebApplicationFactory<Program>` tests: `/healthz` 200 + `status=ok`, `/api/ping` 200, `/api/nope` 404 (not HTML), `/some/route` returns index.html (needs wwwroot fixture) . Delete the tautology.
2. SHOULD-FIX `docker-compose.yml:6-7` — named volume `palabras-appdata` on Unraid lives inside docker.img/docker dir, not in `/mnt/user/appdata`; outside the usual appdata backup plugin and lost if docker.img is rebuilt. Survives `up --build`/recreate (OK), dies on `down -v`/prune. Before M2 (first real DB): switch to bind `${APPDATA:-/mnt/user/appdata/palabras}:/data` (already in `.env.example:21`), host dir chown to uid of `app` (1654 in .NET 8+ images). Dockerfile `/data` chown (`Dockerfile:20`) only helps named volumes.
3. SHOULD-FIX `Backend/Palabras/Program.cs:3-9` — no forwarded-headers/cookie/rate-limit groundwork, and none can be added blindly later. M1 must: `UseForwardedHeaders` (XFF+XFP) with `KnownNetworks/KnownProxies` from `FORWARDED_KNOWN_NETWORK` (`.env.example:17`), placed FIRST in pipeline, before auth and rate limiter; do NOT set `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (currently absent — good). Cookie: `Secure=Always`, name `__Host-…`, Path=/, no Domain, SameSite=Lax — works through TLS-terminating nginx only if scheme is forwarded or SecurePolicy=Always (proxy→app hop is http). Rate limiter partition key must use post-forwarded `RemoteIpAddress`, else all users share the proxy IP. Record in M1 brief; no M0 code needed.
4. NIT `Backend/Palabras/Program.cs:8-14` — static files send no `Cache-Control`. Before M7 SW: `sw.js` and `index.html` must be `no-cache`, `/assets/*` (hashed) `immutable, max-age=31536000`. Use `StaticFileOptions.OnPrepareResponse` then. Also SW scope: `sw.js` must be served from `/`.
5. NIT `Backend/Palabras/appsettings.json:11` — `AllowedHosts: "*"`; set real host at M1 (proxy forwards Host).
6. NIT `Backend/Palabras/appsettings.json:3` — `/data/palabras.db` resolves to `C:\data` on Windows `dotnet run`; add `appsettings.Development.json` override (e.g. `./data/palabras.db`, already gitignored `.gitignore:12`) at M2.
7. NIT `.github/workflows/ci.yml:32` — image built but never run. Add `docker run -d -p 8080:8080` + `curl --retry 10 --retry-all-errors --fail localhost:8080/healthz` and `/api/missing` expect 404. Also `on: push` + `pull_request` double-runs PR branches (`ci.yml:4-5`); restrict `push` to `branches: [main]`.
8. NIT `Dockerfile:10-11` — restore copies only the web csproj; at M2 when Domain/Infrastructure projects or `Directory.Build.props`/`Directory.Packages.props` appear, COPY those too or restore breaks. `global.json` also not copied, so image SDK floats (`sdk:10.0`) while CI/local pin 10.0.401. Acceptable; pin or copy `global.json` if reproducibility matters.
9. NIT `.gitignore:12-13` — bare `data/` and `appdata/` match at any depth (would silently ignore e.g. `Frontend/src/data/`, `content/data/`). Anchor: `/data/`, `/appdata/`.
10. NIT `.env.example:21-23` — `APPDATA`, `PROXY_NETWORK`, `PALABRAS_TAG` unused by compose (dead config, documented as such). Wire APPDATA in finding 2; drop the other two or keep until ΔA override lands.

## Checks (all passed unless listed above)

- csproj `net10.0`, Nullable, ImplicitUsings OK. Program.cs minimal; no `UseHttpsRedirection`/`UseHsts`; no secrets; no forwarded-headers env.
- `/api/{**path}` fallback (`Program.cs:13`) is more specific than `MapFallbackToFile` so `/api/*` 404s; `/healthz` is a real endpoint outranking fallback. Smoke results in implementer report agree. UNVERIFIED: bare `/api` (no slash) routing.
- EF Core: single web project is fine for migrations now; decide at M2 whether DbContext goes to Infrastructure (then `--startup-project` + `MigrationsAssembly`). Not a blocker.
- Dockerfile: 3 stages, deps layers cached (`package*.json`, csproj first), SDK not in runtime, `USER app` after apt step, `EXPOSE 8080`, `ASPNETCORE_URLS`, HEALTHCHECK, `.dockerignore` excludes `.env`, node_modules, bin/obj, local wwwroot; `dist` copied to `/app/publish/wwwroot/` (`Dockerfile:14`) correct.
- Compose: single service, loopback 18480 only, no 80/443/host net/privileged, restart `unless-stopped`, healthcheck, `env_file`. Optional hardening: `security_opt: [no-new-privileges:true]`.
- CI: restore/build/test (slnx, Release), `npm ci`, lint, `npm run build` (= `tsc -b && vite build`, `Frontend/package.json:10`), docker build; Node 24.21.0 + .NET 10.0.401 match global.json/package engines; lockfile cache; read-only perms; no deploy step.
- Hygiene: `.env`, bin/obj, node_modules, dist exist locally but are ignored by `.gitignore`/`.dockerignore`; `.env.example` has blank secrets only. README commands match project layout.

## Missing verification

- CHECKS claim confirmed: `.opencode/runs/T-20261017-1516/03-dotnet-final.log` tail shows build 0 warn/0 err and `Passed! 1/1`, exit=0. CI run result (success) taken from report, not independently re-fetched.
- Committed-state vs working-tree not diffed (shell denied).

verified: re-read OK
