# Palabras — handoff (read this first)

Status date: 2026-10-18 (§4 and §7 checked against `main` at `993f0d1`, run `T-20261018-2005`; §4–§7 M7, deploy and scope items updated at `83d29a5`, run `T-20261018-2105`; M7 rollover gate and test count at `3a6b4c9`; durable cutover, owner-only rollover, build identity, poll image check and deploy-script citation at `c3ce543`; M4 reviewer API, `ReviewGate` migration and test count at `bb1a015`, run `T-20261019-1647`).
First written 2026-10-17 at the end of run `T-20261017-1516` (M0 scaffold + mock UI).
Repo docs are the source of truth. Run reports referenced below are copied into the repo under
`docs/runs/` (see `docs/runs/README.md` for reading order and staleness). They are historical
evidence for state claims, not living docs. Raw `.log` files were not copied; any citation to a
`.log` file (e.g. `runs/11-impl.log`) points at command output that only exists in the original
run workspace, not in this repo.

## 1. Index of `docs/`

| File | Purpose |
| --- | --- |
| `00-handoff.md` | This file. Project summary, locked decisions, current state, constraints. |
| `01-next-steps.md` | Ordered work queue with "done when" tests, MVP vs later. |
| `02-open-questions.md` | Unresolved items, each with a recommended default. |
| `03-auth-bootstrap.md` | ADR: the two accounts come from the CLI `user create` / `set-username` / `set-password` / `list` group run inside the container — never from `.env` seeding — and the login name is renamable because every FK keys on `users.id`. Historical step-25 context; shipped commands/auth in `04-accounts.md` / `05-auth.md`. |
| `design.md` | Original design (copy of run report 01 with T-20261018-0124 corrections struck through or marked inline; frozen original in `runs/01-spanish-pwa-design.md`). |
| `design-amendment.md` | Amendment ΔA–ΔJ that supersedes parts of `design.md` (copy of run report 02 with T-20261018-0124 corrections marked inline; frozen original in `runs/02-design-amendment.md`). Where the two disagree, the amendment wins. `01:NNN` references point at the frozen original. |

Other repo docs: `README.md` (setup/deploy; partly stale, see §6), `AGENTS.md` (agent rules),
`content/style-guide.md` (Spanish content rules), `tools/content-lint/` README (lint usage). Shipped contracts since: `04-accounts.md`, `05-auth.md`, `06-content-cli.md` (content/restore CLI), `07-learn-api.md` (learner API, backups, export), `09-review-api.md` (M4 reviewer API), `restore-drill.md`.

## 2. What the project is

- A private PWA for **one** learner at A2 level, learning **Peninsular (Castilian) Spanish as
  spoken in Pamplona** — not Latin American Spanish. Vocabulary, `vosotros`, and register follow
  `content/style-guide.md`.
- Daily sessions of 5–15 minutes: ES↔EN memory games (recognition, recall, typed answer, etc.)
  scheduled by spaced repetition, moving words from short-term to long-term memory.
- Simple login (username + password, cookie session). No sign-up flow; accounts are seeded.
- A daily reminder at 22:00. The learner may explicitly skip **one day per rolling 7 days**;
  the skip is refunded if a session is completed the same day (`design-amendment.md` ΔC).
- A **reviewer** role for his Spanish fiancée. She has final authority over content: every word,
  sentence and translation is pre-generated with alternatives, and she picks, edits or rejects.
  Only `approved` content ever reaches the learner (ΔD, approved-only gate).
- No LLM at runtime. Content is generated **offline** by a council CLI (`tools/council`, ΔH)
  and imported as `pending_review`.

## 3. Locked decisions

Do not reopen these without the user.

| Area | Decision | Source |
| --- | --- | --- |
| Stack | .NET 10 minimal API + React 19 / TypeScript strict / Vite PWA, one container; API serves the SPA from `wwwroot` | `design.md`, `runs/03-m0-scaffold.md` |
| Storage | SQLite (WAL) via EF Core, from M2 | `design.md`, ΔG |
| Auth | Cookie auth, `PasswordHasher`, roles `learner` / `reviewer` | `design.md`, ΔG M1 |
| SRS | FSRS-6 via Fsrs.Sharp 2.0.0 (pinned `[2.0.0]`) behind `IScheduler`, vendored-source fallback; 21 default weights; golden vectors from py-fsrs 6.3.2 via `tools/fsrs-golden/`; 4 grades (Again/Hard/Good/Easy), desired retention 0.90, 12 new/day | `design.md`, ΔG M5, T-20261018-0124 retention plan |
| Port | **18480** on host and in container. Never 8080 (`git grep 8080` returns nothing) | `runs/08-port-18480.md`, `runs/11-impl.log` |
| Deploy | Clone the private repo `git@github-personal:michaelsieben/palabras.git` to `/mnt/user/repos/palabras` on Unraid, build there with compose | `README.md`, `deploy/unraid-deploy.sh`, `deploy/unraid/docker-compose.yml` |
| Data path | `APPDATA_PATH=/mnt/cache/appdata/palabras` → `/data`. **Never `/mnt/user/...` for SQLite** (FUSE breaks WAL locking) | `README.md`, `design.md` |
| Container | Name and network alias `palabras`; runs as UID 1654 (`chown 1654:1654` the data dir) | `deploy/unraid/docker-compose.yml`, `runs/07-m0-fixes.md` |
| Edge | Nginx Proxy Manager (jc21) host `palabras.siebentower.nl` → `http://palabras:18480` over the external `sieben` bridge network | `deploy/unraid/docker-compose.yml`, `README.md` |
| Publish | `127.0.0.1:18480:18480` only (debug from the host); public traffic goes via NPM | `deploy/unraid/docker-compose.yml` |
| Devices | Android Chrome only: OnePlus 15 (phone) and OnePlus Tab 3 (ultrawide tablet). No iOS work | ΔB |
| Runtime AI | None. Offline council only | ΔH |

Network facts (from `runs/03-unraid-networks.log`, `runs/09-m0-final-review.md`): Unraid host
`192.168.68.66`; NPM on macvlan `br0` at `192.168.68.2` and on `sieben` (`172.18.0.0/16`,
gateway `172.18.0.1`, NPM currently `172.18.0.37` — not stable across restarts).

## 4. What exists today

State on `main` at `993f0d1` (2026-10-18). Milestone status is in §7; this table lists the pieces.

| Piece | State | Evidence |
| --- | --- | --- |
| CI | Two jobs, SDK 10.0.401, Node 24.21.0. `build`: `dotnet restore/build/test` (Release), `npm ci`, `npm run lint`, `npm run build`, `npm test` (16 `node --test` files), upload of `Frontend/dist` as artifact `frontend-dist`, content lint, container build + smoke (`/healthz` ok and `commit` = `$GITHUB_SHA`; anonymous `/api/nope` 401 and login redirect; `/login`, `/offline.htm` 200; seeded owner login, then authenticated `/api/nope` 404, `/api/auth/me`, `/api/learn/words`, `/sesion` 200). `browser` (needs `build`, runs unless cancelled, Playwright `v1.64.1-noble` container, consumes the `frontend-dist` artifact): `npm run test:browser`, then `npm run test:day:browser`. `npm test` and `test:day:browser` were added in `eba9232` | `.github/workflows/ci.yml:11-197`, `Frontend/package.json:13,16,17` |
| Health | `dotnet build` green, `dotnet test` 790 passed / 0 failed (Debug and Release), `npm ci` / lint / build green, content lint `OK — 58 word file(s), 0 errors`; measured at `6e41d0f` (`993f0d1` changed docs only). `docker build` not run locally | `.opencode/runs/T-20261018-2005/01-ci-equivalent-verification.md` (local run artifact) |
| Backend | Forwarded headers first, host filtering (`AllowedHosts` = `palabras.siebentower.nl;localhost;127.0.0.1`), `X-Palabras: 1` on non-GET `/api`, cookie auth with persisted data-protection keys, roles `owner` (also `learner`) and `reviewer`. Endpoints: `/healthz` (503 when SQLite is unreachable, `ee2ef77`; build identity `version`/`commit`/`commitShort`/`milestone`, `commit` = `unknown` unless the build passes `-p:CommitSha`, `7d37bba`), `/api/ping`, `/api/auth/*`, `/api/learn/*`, `/api/learn/stats`, `/api/learn/export`, `/api/learn/day/*` (M7, `83d29a5`); `/api/review/queue`, `/api/review/actions`, `/api/review/approved` (M4, reviewer role only, `e75b850`, `bb1a015`; `09-review-api.md`). Nightly `BackupService`; minute `DayRolloverService`, default OFF behind `M7:DayRolloverEnabled` (`3a6b4c9`), owner account only, durable cutover in `app_meta` (`c7262f8`, `c3ce543`) | `Program.cs:83,97,121-122,126-127,133-142,210-229`, `Learn/DayEndpoints.cs:22-25`, `appsettings.json:8`, `Auth/AccountAuthentication.cs:45`, `Learn/LearnEndpoints.cs:20-27` |
| Database | SQLite via EF Core; migrations `InitialAccounts`, `ContentSchema`, `SrsCards`, `DayStatusAndSkips` (`83d29a5`), `RolloverCutoverMarker` (`app_meta`, `c7262f8`), `ReviewGate` (`word_reviews` + six `words` columns, `3565e75`; six migrations in total). `ReviewGate` is the first migration that rebuilds existing tables (`words`, `word_revisions`) and is not fully covered by the migration transaction (`08-upgrade-rollback.md` section G); the app migrates on startup | `Backend/Palabras/Persistence/Migrations/`, `Program.cs:125` |
| CLI | `user create` / `set-username` / `set-password` / `list`, `import`, `content approve/status/retire`, `restore-progress` | `04-accounts.md`, `06-content-cli.md`, `07-learn-api.md` |
| Backend tests | 17 files in `Backend/Palabras.Tests/` (M0 API, M1 auth, M2 content, M5 SRS/queue/learn API incl. py-fsrs golden vectors, M6 stats, M7 day status, M10 backup, CLI); test totals change with almost every commit, so each is tied to its commit: 790 at `6e41d0f`, 825 at `83d29a5` (`10-m7-backend.md` §4), 854 at `3a6b4c9` (`03-rollover-gate.md` §5), 858 at `c7262f8`, 874 at `7d37bba`, 882 with the rollover fixes on top of `7d37bba` before the content batch `664e038` landed; with that batch, 872 pass / 10 fail, all fixed corpus-size assertions (`18-rollover-fixes.md` §8). At `bb1a015`: 19 files (adds `M4ReviewSchemaTests.cs`, `M4ReviewApiTests.cs`), 910 passed / 0 failed on SDK 10.0.402 (`T-20261019-1606/15-verify-dto-claims.md` §3). Run `dotnet test` for the current figure | `Backend/Palabras.Tests/`, step-01 artifact above |
| Frontend | Login; learner routes `/` (`/sesion`), `/palabras`, `/progreso`, `/ajustes` on the real API, with IndexedDB offline attempt queue (`21f1d52`). Reviewer routes `/revisar`, `/aprobadas` remain mock data. Dev-only `/debug/viewport` | `Frontend/src/App.tsx:27,54,64`, `Frontend/src/ReviewQueue.tsx` |
| Frontend tests | 16 `node --test` files (`npm test`, `tests/*.test.mjs`) and Chromium scripts: `npm run test:browser` (`browser.mjs`, which drives the offline e2e `learnBrowser.mjs` and the day/day-panel flows, then `sw-navigation.mjs`) and `npm run test:day:browser` (`dayBrowser.mjs`, M7 day-status subset). All three scripts run in CI (see CI row). `liveBrowser.mjs` and `seedLiveBrowser.mjs` are not wired into `package.json` or CI | `Frontend/tests/`, `Frontend/package.json:13,16,17` |
| PWA shell | Manifest (es-ES), service worker cache `palabras-shell-<sha>` with atomic-install auto-reload update flow (navigation authority = `GET /api/auth/me`, no app document precached), ultrawide tablet layout; `index.html`, `sw.js`, manifest served `no-cache`, manifest as `application/manifest+json` | `runs/13-pwa-ultrawide.md`, `Frontend/HOSTING.md`, `Frontend/tests/sw-navigation.mjs`, `Program.cs:190-203` |
| Content | `content/` schema v2 with lifecycle `status` enum, `style-guide.md`, `style-guide.lexicon.json`, word files under `content/words/`. The corpus grows per content batch (58 files at `993f0d1`; `664e038` added a 171-word batch), so no count here is current: `node tools/content-lint/index.js` prints it. Tests that hard-coded the corpus size are being de-hardcoded for the same reason (T-20261018-2105 step 20, `80e17c5`) | `content/words/`, `tools/content-lint/` |
| Deploy | `deploy/unraid/docker-compose.yml` (production), root `docker-compose.yml` (local), `deploy/unraid-deploy.sh` main-branch poll with data/account guards (`2e6ac48`), running-image comparison and outgoing-image preservation as `palabras:rollback-<id12>`, newest 2 kept (`1a90d4c`), and smoke test `deploy/tests/unraid-deploy-smoke.sh`. A production container has run on Unraid (data-loss incident 2026-10-18); which commit it runs is not recorded in the repo | `08-upgrade-rollback.md:27-40,285-289` |

**Historical — M0 snapshot (2026-10-17, run `T-20261017-1516`).** Kept for the run-report trail; superseded
by the table above.

| Piece | State | Evidence |
| --- | --- | --- |
| Repo + CI | Private GitHub repo, CI green (run 39037127620 on commit 561de60); SDK 10.0.401, Node 24.21.0 | `runs/03-m0-scaffold.md` |
| Backend | `Backend/` minimal API: `/healthz`, `/api/ping`, `/api/{**path}` → 404, SPA fallback; no-cache headers on `sw.js` and `manifest.webmanifest`. No forwarded headers, auth, EF or DB yet. `AllowedHosts` is `"*"` | `Program.cs`, `runs/09-m0-final-review.md` |
| Backend tests | `M0ApiTests.cs` has 8 tests. Reports 06/10 say 7; the 8th (`PwaShellFilesReturnNoCache…`) was added later without a report and its pass status is unverified | `runs/06-m0-tests.md`, `runs/10-m0-shouldfix.md` |
| Container | Dockerfile builds and smoke-tests locally (bind mount, UID 1654) | `runs/07-m0-fixes.md`, `runs/08-port-18480.md`, `runs/11-impl.log` |
| Frontend | Full mock UI, Spanish (es-ES): routes `/`, `/sesion`, `/palabras`, `/revisar`, `/aprobadas`, `/ajustes`, dev-only `/debug/viewport`. Mock data in `Frontend/src/mock/demo.ts`. No API, auth, FSRS, IndexedDB or push. All checks green (type check, lint, build, 6/6 unit, Chromium browser tests) | `runs/13-ui.md`, `runs/13-ui.log` |
| PWA shell | Manifest (es-ES), service worker cache `palabras-shell-<sha>` with prompt-to-refresh update flow, ultrawide tablet layout | `runs/13-pwa-ultrawide.md` |
| Content | `content/` schema v2 with lifecycle `status` enum, `style-guide.md`, `lexicon.json`, 58 word files (34 A1, 24 A2), all `pending_review`. Lint: `OK — 58 word file(s), 0 errors, 0 warning(s)` | `content/`, `tools/content-lint/` (no run report) |
| Deploy files | `deploy/unraid/docker-compose.yml` (production) and root `docker-compose.yml` (local, `./data`, no `sieben`) | repo |

At M0 handoff time (2026-10-17) `Frontend/**`, `content/**`, `tools/**` and some backend changes were
**uncommitted** in the working tree while another agent was committing. Check `git status` and
`git log` before assuming anything is on `main`.

## 5. What does not exist yet

- ~~Deployment on Unraid: nothing is running; the compose folder does not exist on the server.~~ Done: deployed
  2026-10-17 (`README.md:60-64`, folder `README.md:71`); data-loss incident 2026-10-18 (`08-upgrade-rollback.md:285-289`).
  Which commit production runs is not recorded in the repo.
- ~~Forwarded headers, host filtering, auth, roles, data protection keys (M1).~~ Done; see §7.
- ~~EF Core model, migrations, importer, revisions (M2).~~ Done; see §7.
- Council CLI (M3), reviewer gate UI backed by the API (M4; the API itself shipped in `e75b850`), ~~FSRS (M5)~~, real sessions (M6, in
  progress), ~~day status / streak / skip frontend~~ (M7 wiring shipped `0303a87`, server-date and refresh corrections T-20261019-1508), Web Push (M8), ~~backups (M10)~~. Struck items
  shipped; see §7.
- ~~Content lint in CI~~ Done: `Lint content` step in `.github/workflows/ci.yml:54-55`.
- Real-device measurements on the OnePlus 15 / Tab 3 (`runs/13-pwa-ultrawide.md`).

## 6. Disagreements between sources

Resolved here so the next session does not trip over them.

1. **Proxy path.** ΔA proposed a loopback bind plus a private `palabras_net`. Reports 04 and 08
   used a LAN `0.0.0.0` publish with NPM upstream `192.168.68.66:18480`. The **current repo**
   (`127.0.0.1` publish + `sieben` network, upstream `palabras:18480`) is authoritative.
2. **Trusted proxy.** `runs/09-m0-final-review.md` item 13 suggests `KnownProxies = 192.168.68.2`
   (NPM's macvlan IP). That is wrong for the current path: traffic arrives over `sieben`, so the
   source is NPM's `172.18.x.x` address, which changes. Use `KnownIPNetworks = 172.18.0.0/16`
   (as `README.md` says). `KnownProxies` accepts single IPs only; `KnownNetworks` is obsolete
   in .NET 10 (ASPDEPR005).
3. **Test count.** Reports say 7; the M0 snapshot had 8 (see §4). Resolved: `M0ApiTests.cs` now has 9 `[Fact]`s and
   the whole suite ran 854 tests, 0 failed, at `3a6b4c9` (825 at `83d29a5`; `T-20261018-2105/03-rollover-gate.md` §5); later totals per commit in §4.
4. **Cache headers.** `runs/13-ui.md` says no-cache headers for `sw.js`/manifest are missing. Resolved:
   `Program.cs:199-201` sends `no-cache` for `index.html`, `sw.js` and the manifest, the SPA fallback uses the same
   options (`:231`), and the manifest is served as `application/manifest+json` (`:192`).
5. **Bare `/api`.** `runs/07-m0-fixes.md` saw 404; `runs/09-m0-final-review.md` says bare `/api`
   falls through to the SPA. Verify with a test in M1; desired behaviour is 404.
6. **Compose folder spelling.** Resolved: `palabras`. `README.md:71,74`, `AGENTS.md` and
   `deploy/unraid-deploy.sh:32` all use `/mnt/user/docker/palabras/`, created during setup
   (`02-open-questions.md` Q1).
7. **README is stale.** It still describes a placeholder screen, deferred manifest and the old
   milestone numbering (M7 = service worker, M8 = backups). Use ΔG numbering (§7). README is
   owned by other agents; do not edit it from a docs-only task.

## 7. Milestones (ΔG numbering; State updated 2026-10-18, see `01-next-steps.md` Status)

MVP = M0–M10. Later = L1–L6. Full text in `design-amendment.md` ΔG.

| ID | Scope | State |
| --- | --- | --- |
| M0 | Repo, CI, deploy hello | Done; deployed on Unraid 2026-10-17 (`README.md:60-64`) |
| M1 | Auth + roles + edge | Done (T-20261017-1516) |
| M2 | Content schema v2 + lifecycle + importer + lint + revisions | Implemented; content lint runs in CI (`ci.yml:54-55`) |
| M3 | Council CLI (`tools/council`) | Not started |
| M4 | Reviewer gate `/revisar` | Backend implemented: migration `ReviewGate` (`3565e75`), reviewer API queue / actions / approved list (`e75b850`, approved-list `version` in `bb1a015`; `09-review-api.md`). Reviewer UI still mock data on `main` at `bb1a015`. Not done: re-import-after-edit tests (plan step 4), verdict export (step 5). Not deployed; the first deploy runs the first table-rebuild migration (`08-upgrade-rollback.md` section G) |
| M5 | FSRS-6 + cards created on approval (parallel after M2) | Implemented |
| M6 | Session composer + exercises 1–4 + offline | In progress (composer API, session UI and IndexedDB offline attempt queue + Chromium e2e shipped, `21f1d52`; Android Chrome device check not done, checklist in `Frontend/HOSTING.md`). Exercises 1–2 and the timer/estimate are replaced by owner decision (`07-learn-api.md` deviations) |
| M7 | Day status, streak, rolling skip | Backend implemented (`83d29a5`, `07-learn-api.md` M7 section); rollover gate default OFF, backfill guard and bounded `localDate` on `day/done` added (`3a6b4c9`). Cutover made durable in `app_meta` (`c7262f8`); `missed` history restricted to the owner account (`c3ce543`). Frontend wired (`0303a87`; server plan date and live status refresh corrections T-20261019-1508). Rollover **not enabled**: operator decision still gated on deployed client/device and job evidence (`07-learn-api.md` "Not covered yet", `08-upgrade-rollback.md` §D). Timezone-change acceptance remains unavailable: no zone-setting API |
| M8 | Web Push at 22:00 | Not started |
| M9 | Wide-tablet learner layout | Mock layout done; device check pending |
| M10 | Backups + restore | Implemented |
| L1–L6 | Listening, council report/rework, stats, FSRS optimiser, passkeys, ICS/webhook | Later |

ΔJ: no open question blocks M1.

## 8. M1 sequencing constraints

These are ordering rules, not suggestions. Getting them wrong breaks login behind NPM.

1. **Forwarded headers first.** `app.UseForwardedHeaders()` must be the first middleware,
   before static files, auth and anything that reads scheme/host/IP.
   `ForwardedHeaders = XForwardedFor | XForwardedProto | XForwardedHost`.
2. **Trust only the proxy network.** Clear the defaults and add
   `KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.18.0.0/16"))` (the `sieben` CIDR).
   Note: every container on `sieben` is then trusted; see `02-open-questions.md` Q4.
3. **AllowedHosts** = `palabras.siebentower.nl;localhost;127.0.0.1`. Keep `localhost` and
   `127.0.0.1` so the healthcheck and host-side debugging still work.
4. **Never `UseHttpsRedirection()` or `UseHsts()`.** TLS terminates at NPM; the container
   speaks plain HTTP. Redirection would loop.
5. **Cookie** named `__Host-palabras`, `SecurePolicy = Always`, `HttpOnly`, `SameSite=Lax`,
   `Path=/`, no `Domain`, sliding 30-day expiry (`design.md` §auth). `__Host-` only works if
   forwarded headers already set the scheme to `https` (rule 1). CSRF: require header
   `X-Palabras: 1` on every non-GET `/api` call; no CORS (`design.md`).
6. **Data protection keys** persisted to `/data/keys` (`PersistKeysToFileSystem`), so cookies
   survive container rebuilds. `/data` is the `APPDATA_PATH` bind mount owned by UID 1654.
7. API endpoints return 401/403 (not redirects) on auth failure; the SPA handles login routing.

## 9. Where to look per subsystem

| Subsystem | Read |
| --- | --- |
| Overall design, data model draft, exercise types | `design.md` |
| Overrides: devices, reminders, skip, lifecycle, council, milestones | `design-amendment.md` (ΔA–ΔJ) |
| Scaffold, CI, GitHub, SDK versions | `runs/03-m0-scaffold.md` |
| Port 18480, container smoke | `runs/08-port-18480.md`, `runs/08-container-smoke.log` |
| Container user, bind mount | `runs/07-m0-fixes.md` |
| Backend review findings (incl. edge items) | `runs/05-m0-review.md`, `runs/09-m0-final-review.md`, `runs/10-m0-shouldfix.md` |
| Backend tests | `runs/06-m0-tests.md`, `Backend/Palabras.Tests/M0ApiTests.cs` |
| Unraid networks and ports | `runs/03-unraid-networks.log`, `runs/03-unraid-ports.log` |
| Frontend routes, components, checks | `runs/13-ui.md`, `runs/13-ui.log` |
| PWA shell, service worker, ultrawide layout | `runs/13-pwa-ultrawide.md` |
| Content rules | `content/style-guide.md`, `content/*.schema.json`, `tools/content-lint/` |
| Deploy | `deploy/unraid/docker-compose.yml`, `.env.example`, `README.md` (mind §6.7) |

Runs 12, 14 and 15 left no report. Run 11 left only `11-impl.log` (docker build, `git grep 8080`
exit 1, docker credential helper worked around with an empty `DOCKER_CONFIG`).
