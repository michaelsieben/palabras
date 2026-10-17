# 09 M0 final review

Verdict: no blockers. STATUS ok with should-fix items (cheap, recommended before M0 done). M0 acceptance met.
Verified: re-read OK. Test log tail confirmed: `08-dotnet.log:9` `Passed: 7, Failed: 0`, `exit=0`.
Not verified (no shell, review is read-only): actual `docker build`/`docker compose up`/CI run; UNVERIFIED that the smoke step passes on a real runner.

## (a) Bind mount
- `.gitignore:12` `/data/`, `.dockerignore:10` `/data/` both present. OK.
- Compose `${APPDATA_PATH:-./data}` (`docker-compose.yml:15`): `:-` treats empty value (`.env.example:22` `APPDATA_PATH=`) as unset, so default applies. OK.
- README tells user to `mkdir` + `chown 1654:1654` before first run (`README.md:75-76`) and notes Dockerfile chown is hidden by the bind mount (`:68`). UID 1654 is the `app` user in .NET 8+ images. Warning about setting before first deploy is accurate (`README.md:66`, `.env.example:21`).
- M0 never writes `/data` (healthz does not touch it), so wrong ownership cannot break M0.
1. SHOULD-FIX `README.md:83`: local-dev case only says "ensure writable by 1654 before M2". If `./data` is absent Docker creates it root-owned and M2 fails with `unable to open database file`. Fix: add `mkdir -p data && chmod 777 data` (or `sudo chown 1654:1654 data`) to Local dev steps, and in the M2 brief add a startup write-probe to `/data` that fails fast with a clear message.
2. SHOULD-FIX (M2 risk) `README.md:62-66`, `.env.example:20`: `/mnt/user/appdata/...` goes through Unraid shfs (FUSE). SQLite WAL/locking on shfs is a known source of corruption/lock errors. Fix: document `/mnt/cache/appdata/palabras` (or the disk path) as the recommended `APPDATA_PATH`, or force `journal_mode=DELETE` in M2. Must be decided before M2, note it in M2 milestone notes.

## (b) Port change
- Zero `8080` matches in README, compose, Dockerfile, CI, `.env.example`, sources (grep, md/yml/json/cs/ts/etc.). OK.
- `EXPOSE 18480` (`Dockerfile:25`), `ASPNETCORE_URLS` (`Dockerfile:23`), Dockerfile HEALTHCHECK (`:27`), CI (`ci.yml:36-40`) all 18480.
- Precedence: compose `environment:` overrides image `ENV` (`docker-compose.yml:13`), and `env_file` has lower precedence than `environment`. Published port `docker-compose.yml:10` and listen port both derive from the same `${APP_PORT:-18480}`, so no drift. Compose healthcheck (`:19`) overrides the Dockerfile one, so the hardcoded 18480 in `Dockerfile:27` is correct only for plain `docker run` default.
3. NIT `docker-compose.yml:17`: `env_file: .env` also injects `APP_PORT`/`APPDATA_PATH` into the container. Harmless (ASP.NET ignores both) but unneeded; fine to leave.
4. NIT `Dockerfile:27`: HEALTHCHECK hardcodes 18480; a `docker run -e ASPNETCORE_URLS=...` user without compose would see unhealthy. Acceptable; one-line comment would suffice.

## (c) Tests (`M0ApiTests.cs`)
- Tests 1,2,4 assert behaviour (status, content type, body). Test 2 also pins the exact property set; fine as a contract test.
- Test 3 (`:44-55`) is meaningful: test 4 proves the custom webroot with `index.html` is honoured, so without `MapFallback("/api/{**path}")` the same request would be 200 + SPA marker. 404 + no marker cannot pass for the wrong reason. Residual gap: bare `/api` (no trailing slash) does not match `/api/{**path}` (`Program.cs:13`) and falls to SPA. NIT 5: add a test/route for `/api`.
- Test 5 (no HTTPS redirect, `:71-82`) is meaningful: `https_port=443` is set (`:138`), so adding `UseHttpsRedirection` would produce a 307 and fail it.
6. SHOULD-FIX `M0ApiTests.cs:84-95` (HSTS): vacuous. HSTS middleware only emits on HTTPS requests and skips localhost; this test issues `http://localhost`, so it would pass even if `UseHsts()` were added. Fix: set `BaseAddress = https://palabras.example.test` for this one test (TestServer honours the scheme) and assert header absent; drop the `RequestUri.Scheme == "http"` assertion there.
7. NIT `:97-110`: `HealthzReturnsOkWhenDataDirectoryDoesNotExist` is environmental and mostly vacuous in M0 (nothing reads the connection string). `Directory.Exists("/data")` at `:102` fails if tests ever run where `/data` is mounted (e.g. inside the app container) and resolves to `C:\data` on Windows. Fix: drop that assert; revisit at M2.
8. NIT `:137`: `UseSetting("ConnectionStrings:Default", ...)` is host-level config; in minimal hosting `appsettings.json` may take precedence. UNVERIFIED. Isolation of real `/data` today holds only because nothing opens the DB. In M2 use `ConfigureAppConfiguration`/`ConfigureServices` and add a test asserting the effective connection string.
9. NIT `:23,40`: version asserted only non-empty; `"unknown"` fallback (`Program.cs:6`) would pass. Seven separate factories is slow-ish; use `IClassFixture` when the suite grows.

## (d) CI smoke (`ci.yml:33-47`)
- Port consistent (18480). Bind to 127.0.0.1 on runner is fine.
- Failures propagate: default `bash -e`, so a failed `[[ ]]` or failed `$(curl)` assignment fails the step. Cleanup step has `if: always()`, logs then `docker rm -f`; does not mask failure (job stays red).
- Retry: `--retry 30 --retry-delay 1 --retry-all-errors --fail` gives ~30 s startup window; reasonable.
10. SHOULD-FIX `ci.yml:37-40`: no `--max-time` on any curl and no `timeout-minutes`. A container that accepts the TCP connection but never answers hangs the step until the 6 h job default. Fix: add `--max-time 5` to all three curls and `timeout-minutes: 10` on the job (or step).
11. NIT `ci.yml:34`: add `shell: bash` explicitly (gets `pipefail`; makes the `[[ ]]` assumptions explicit). Smoke does not assert image runs as non-root or that Docker health reaches `healthy`; optional `docker inspect --format '{{.State.Health.Status}}'` loop.

## (e) M1+ regression risks
12. SHOULD-FIX (doc, `README.md:119`): the M1 note says replace wildcard `AllowedHosts` with the real host, but the container HEALTHCHECK (`Dockerfile:27`, `docker-compose.yml:19`) and CI smoke (`ci.yml:37-40`) request `localhost`/`127.0.0.1`. Host filtering would answer 400, the container goes unhealthy and CI fails. Fix: add to the M1 note: keep `localhost`/`127.0.0.1` in `AllowedHosts` (or send `-H "Host: palabras.siebentower.nl"` in healthcheck and CI) and add a test for it.
13. Forwarded headers: `0.0.0.0:18480` (`docker-compose.yml:10`) means any LAN client can reach the app directly, bypassing nginx and spoofing XFF/XFP. `KnownProxies` must be the exact proxy IP (192.168.68.2 as seen after DNAT, confirm), not the LAN /24. Direct hits then carry no trusted XFP, so `__Host-`/`Secure=Always` cookies are not accepted over plain HTTP, which is the desired failure mode. TLS terminates at nginx; do not add `UseHttpsRedirection`/`UseHsts` in the app (tests 5/6 guard this; fix 6 first).
14. Bind mount + EF/SQLite: dir (not just file) must be writable by 1654 for `-journal`/`-wal`/`-shm`; see items 1-2. Data Protection key ring must persist under `/data/keys` or cookies invalidate on restart.

## Missing verification
- No docker/compose/CI execution evidence in handovers (docker steps not run in review; shell denied). Test log is present and matches claim.
