# 10 M0 SHOULD-FIX follow-up (T-20261017-1516)

Input: `09-m0-final-review.md` (0 blockers, 4 SHOULD-FIX, 10 NITs).
Scope: exactly the 4 SHOULD-FIX items, plus 2 one-line documentation/consistency edits that fall
inside item 3 (listed below). No runtime, contract, config or deployment-behavior change.
Repo: `D:\Visual Studio Repo\Personal\Palabras`. Modules read: `dotnet.md` (C#/test change).

## Items applied

### 1. HSTS test that could not fail -> real guard (SHOULD-FIX, review item 6)
`Backend/Palabras.Tests/M0ApiTests.cs`

- Test renamed `PlainHttpRequestDoesNotEmitHstsHeader` -> `HttpsRequestDoesNotEmitHstsHeader`.
- It now drives `https://palabras.example.test` (new optional `CreateHttpClient(string baseAddress
  = "http://localhost")`; existing callers unchanged) and asserts `Scheme == "https"` and
  `Host == "palabras.example.test"` before asserting the header is absent.

Why the assertion is meaningful now (not an always-green test):
`HstsMiddleware` only adds `Strict-Transport-Security` when `Request.IsHttps` is true **and** the
request host is outside `HstsOptions.ExcludedHosts` (localhost, 127.0.0.1, [::1]). The old test used
plain `http://localhost`, i.e. wrong on both counts, so it passed regardless of `UseHsts()`.
`WebApplicationFactory`/TestServer honours the client `BaseAddress` scheme, so this request is
handled as HTTPS and the non-localhost host defeats the exclusion list.

Proven, not assumed - mutation check (raw output in `10-impl.log`):
temporarily adding `app.UseHsts();` to `Program.cs:4` made the test FAIL
(`Failed HttpsRequestDoesNotEmitHstsHeader ... Assert.False() Failure Expected: False Actual: True`,
exit 1); the mutation was then reverted and `Program.cs` is byte-identical to HEAD
(`git diff --stat` lists no `Program.cs`). The `Scheme`/`Host` assertions stop the test going vacuous
again if the base address is ever reverted to `http://localhost`.

Redirect (307/308) coverage is unchanged and still meaningful: `PlainHttpRequestDoesNotRedirectToHttps`
(test 5) asserts scheme `http`, status `200` and no `Location` header - `https_port` is set to `443`
in the factory, so adding `UseHttpsRedirection()` there would yield a redirect and fail it.

Not done (deliberately): the review's alternative "send `X-Forwarded-Proto: https`" was rejected for
this test - M0 has no `UseForwardedHeaders` (M1 work), so XFP is ignored and an XFP-based request
would be plain HTTP, i.e. it would reintroduce the same vacuous assertion.

### 2. CI hang protection (SHOULD-FIX, review item 10)
`.github/workflows/ci.yml`

- `timeout-minutes: 10` on the `build` job (with a comment on why).
- `--connect-timeout 2 --max-time 5` added to all three curls in the smoke step (healthcheck body,
  `/api/nope` status, `/anything` status). Worst case for the retrying curl is bounded at ~150 s.
- Cleanup step unchanged: `if: always()`, logs then `docker rm -f`.
- NIT taken: `shell: bash` added to the smoke step (one line; GitHub already defaults to `bash` with
  `pipefail` on `ubuntu-latest`, and the script has no pipelines, so no behavioural change - it makes
  the `[[ ]]` assumptions and `pipefail` explicit).

### 3. Unraid path: shfs -> cache pool (SHOULD-FIX, review item 2)
`README.md:66, 74-76, 115`, plus two consistency one-liners (see "Extra one-liners")

- Documented `APPDATA_PATH=/mnt/cache/appdata/palabras` instead of `/mnt/user/appdata/palabras`.
- Reason documented in place: `/mnt/user` is the shfs/FUSE layer and SQLite locking through shfs is a
  known source of lock errors/corruption; `/mnt/cache/...` is direct disk access, which is what a
  SQLite file needs. The `appdata` share must still exist (and stay in the backup strategy) because
  `/mnt/cache/appdata/palabras` and `/mnt/user/appdata/palabras` are the same directory; pool-name
  caveat added ("or whichever pool/disk holds the `appdata` share").
- `mkdir -p` / `chown 1654:1654` instructions updated to the same new path.
- Backup/restore section now says: keep the cache-pool path inside the `appdata` share and include
  that share in the appdata backup strategy.

Extra one-liners taken for consistency with item 3 (both documentation only, no behavior change):
- `.env.example:20-21` comment still recommended `/mnt/user/appdata/palabras`, which would have
  contradicted the README; now points at `/mnt/cache/appdata/palabras` and names the shfs risk.
- `README.md:115` (backup paragraph) cross-referenced the old shfs path; reworded as above.

### 4. `AllowedHosts` would 400 the healthcheck (SHOULD-FIX, review item 12)
`README.md:119` (M1 note)

- Now: replace wildcard `AllowedHosts` with the real host **plus `localhost;127.0.0.1`**
  (example `palabras.siebentower.nl;localhost;127.0.0.1`), because the container healthcheck
  (`docker-compose.yml:19`, `Dockerfile:27`) and the CI smoke step request `http://localhost` /
  `127.0.0.1`; dropping them answers 400, marks the container unhealthy and fails CI.
- Alternative recorded too: send an explicit `Host: palabras.siebentower.nl` header from both.
- Also asks for a test covering the healthcheck host (M1 work, not M0).

## Verification (all run from `D:\Visual Studio Repo\Personal\Palabras`)

| Command | Result |
| --- | --- |
| `dotnet test Backend/Palabras.slnx -c Release` | `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 179 ms`, `EXIT=0` |
| `dotnet test ... --filter HttpsRequestDoesNotEmitHstsHeader` with temporary `UseHsts()` | `Failed: 1, Passed: 0`, `EXIT=1` (mutation reverted afterwards) |
| `docker compose --env-file .env.check config --quiet` | exit 0, no output |
| `git grep -n 8080` (tracked files) | 0 matches |
| `grep -rn 8080` excluding `node_modules` | 0 matches (the only hits repo-wide are 15 inside gitignored `Frontend/node_modules`) |
| `actionlint` | NOT AVAILABLE on this machine (`command -v actionlint` -> not found) |
| YAML parse of `.github/workflows/ci.yml` (Python + PyYAML) | OK: 1 job `build`, `timeout-minutes = 10`, 11 steps, smoke `shell: bash`, all 3 curls carry `--connect-timeout 2 --max-time 5` |
| `bash -n` on the extracted smoke script | `BASH_SYNTAX_OK` |

Test count: 7 (not 8+). No test was added or removed - item 1 renamed and strengthened an existing
test, and the review's NIT 5 (`/api` bare-path test) was not taken because it would pin current
behaviour of a route the review itself questions, which is a scope/design call, not a mechanical edit.

Raw log: `.opencode/runs/T-20261017-1516/10-impl.log`.

## Files changed

- `Backend/Palabras.Tests/M0ApiTests.cs` (item 1)
- `.github/workflows/ci.yml` (item 2 + NIT 11)
- `README.md` (items 3, 4)
- `.env.example` (item 3 consistency one-liner)

`Program.cs` untouched at HEAD (mutation check only, reverted).

## NITs not taken (unchanged from review)

1 (README local-dev `data/` ownership), 3 (`env_file` leaking `APP_PORT`/`APPDATA_PATH`), 4 (Dockerfile
healthcheck comment), 5 (`/api` bare path), 7 (`/data` assert), 8 (`UseSetting` vs `appsettings.json`),
9 (version `"unknown"` fallback / factory reuse), 11 partially taken (`shell: bash` only),
smoke non-root/healthy assertions. Only NIT 11 was one line with no behavioural risk.

## Residual risks / follow-ups

- The smoke step still has no positive proof that `docker build`/`docker run` work on the runner; a CI
  run is the only real validation. Not available here.
- CI `timeout-minutes: 10` assumes build+test+npm+image build complete within 10 min; cold-cache
  runners should be well inside that, but the first observed run should be checked.
- Item 4 is a documented requirement for M1, not implemented in M0 (M0 keeps wildcard `AllowedHosts`).
