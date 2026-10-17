# T-20261017-1516 / 06 — M0 API integration tests

STATUS: ok. Seven real integration tests replace the arithmetic placeholder. No application defects exposed.

## Scope and files

Paths below are relative to `Palabras/`.

- Added `Backend/Palabras.Tests/M0ApiTests.cs`: xUnit tests using `WebApplicationFactory<Program>` and xUnit assertions.
- Updated `Backend/Palabras.Tests/Palabras.Tests.csproj`: test-only `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 dependency; target remains `net10.0`.
- Deleted `Backend/Palabras.Tests/ScaffoldTests.cs`: arithmetic placeholder only; no existing behavior assertion weakened.
- Updated `Backend/Palabras/Program.cs:18`: added `public partial class Program { }`, the only application-code edit. Endpoint and middleware behavior unchanged.
- Read root `AGENTS.md` and review findings before writing tests. No repo testing-rules file, nested AGENTS.md, assertion/mocking convention, or existing fixtures were found. Retained existing xUnit framework and assertions; no mocking library introduced.

## Test matrix

1. Health contract — `HealthzReturnsOkStatusAndNonEmptyVersion`: `/healthz` returns 200, `application/json`, `status == "ok"`, non-empty string `version`.
2. Ping contract — `PingReturnsDocumentedStatusAndVersionJsonShape`: `/api/ping` returns 200 JSON with exactly `status` and `version`, status `ok`, non-empty version. Contract from README.md:52. Property order is not constrained; release version is not hardcoded.
3. API fallback isolation — `UnknownApiRouteReturnsNotFoundInsteadOfSpaDocument`: `/api/does-not-exist` returns 404, not HTML, and not the SPA marker. Fixture supplies a real index.html, so lack of frontend files cannot mask an API fallback regression.
4. SPA fallback — `ClientRouteReturnsSpaHtml`: `/some-spa/route` returns 200, `text/html`, and exact fixture index.html content through the app's actual static-file fallback.
5. HTTP proxy contract — `PlainHttpRequestDoesNotRedirectToHttps`: explicitly HTTP request, automatic redirects disabled, 200, no `Location` header. Test host provides HTTPS port 443 so accidental HTTPS middleware would have a redirect target rather than silently bypassing redirection.
6. HTTP HSTS contract — `PlainHttpRequestDoesNotEmitHstsHeader`: explicitly HTTP request, 200, explicit absence of `Strict-Transport-Security`.
7. Health independent of data — `HealthzReturnsOkWhenDataDirectoryDoesNotExist`: asserts literal `/data` absent in test environment, configures database path beneath a unique absent data directory, checks 200 and that the directory remains absent. Never deletes or mutates host `/data`.

Each test creates and disposes its own factory/client/response. Host runs in Production, serving an isolated temporary wwwroot populated with a test-owned HTML file. Temporary files are removed after host disposal. No frontend build, Docker, DB, shared environment variables, timing waits, retries, or external network required. Requests and content reads use await and explicit cancellation tokens.

## Executed checks

- SDK: `dotnet --version` -> `10.0.401`; `.opencode/runs/T-20261017-1516/06-sdk.log`.
- Command, from `Palabras/`: `dotnet test Backend/Palabras.Tests/Palabras.Tests.csproj --logger "console;verbosity=normal"`.
- Actual result: 7 total, 7 passed, 0 failed, 0 skipped; exit code 0. Runtime reported .NET 10.0.12. Build produced no reported warnings/errors.
- Full, unmodified restore/build/test output: `.opencode/runs/T-20261017-1516/06-tests.log` (80 lines, including all test names and pass counts).
- Red/green observation: all seven tests observed PASS on the existing correct M0 behavior. No FAIL against an unfixed application defect was observed: this task backfills coverage, not an app fix. Production routing/TLS mutations were not made because only exposing Program was authorized. No pre-fix failure claim.

## Findings and remaining gaps

- Application defects exposed: none. Existing `/api/{**path}` fallback at `Backend/Palabras/Program.cs:13` correctly rejects the tested unknown API route while SPA fallback remains functional.
- Test 7 intentionally requires an environment without a literal `/data` directory. A runner mounting `/data` will fail the explicit prerequisite assertion; tests will not erase host data to satisfy it.
- SPA document is test-owned: verifies server fallback, not React output or Docker packaging. Published image, live nginx, forwarded headers, and HTTPS-origin behavior are outside this test scope. HSTS absence assertion is specifically for plain HTTP, as requested.
- Bare `/api`, other verbs, static assets, auth, database, and future milestones are not covered by these seven tests.
- No Dockerfile, compose, README, `.github/`, deployment, or remote push changes made.

## Local commit and handoff

Repository already had a scaffold commit on main. Committed only these four owned source/test paths locally: `7cb2351 test(api): cover M0 endpoints and SPA fallback`. Git output: `.opencode/runs/T-20261017-1516/06-git.log`.

Other agent changes remain dirty and untouched: `.env.example`, `AGENTS.md`, `README.md`, `docker-compose.yml`. Artifacts live under the parent workspace `.opencode/`, outside the Palabras Git repository.

NEXT: dotnet-reviewer — review test-only commit and confirm M0 coverage finding resolved.
