# Palabras — handoff (read this first)

Status date: 2026-10-17. Written at the end of run `T-20261017-1516` (M0 scaffold + mock UI).
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
| `03-auth-bootstrap.md` | ADR: the first two accounts are created by the CLI `set-password`, not by env seeding — follow it in M1. |
| `design.md` | Original design (verbatim copy of run report 01; frozen original in `runs/01-spanish-pwa-design.md`). |
| `design-amendment.md` | Amendment ΔA–ΔJ that supersedes parts of `design.md` (verbatim copy of run report 02; frozen original in `runs/02-design-amendment.md`). Where the two disagree, the amendment wins. |

Other repo docs: `README.md` (setup/deploy; partly stale, see §6), `AGENTS.md` (agent rules),
`content/style-guide.md` (Spanish content rules), `tools/content-lint/` README (lint usage).

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
| SRS | FSRS-6 ported to C#, 21 weights, golden vectors from `ts-fsrs` | `design.md`, ΔG M5 |
| Port | **18480** on host and in container. Never 8080 (`git grep 8080` returns nothing) | `runs/08-port-18480.md`, `runs/11-impl.log` |
| Deploy | Clone the private repo `https://github.com/sunmsieben/palabras` to `/mnt/user/repos/palabras` on Unraid, build there with compose | `runs/03-m0-scaffold.md`, `deploy/unraid/docker-compose.yml` |
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

At handoff time `Frontend/**`, `content/**`, `tools/**` and some backend changes were
**uncommitted** in the working tree while another agent was committing. Check `git status` and
`git log` before assuming anything is on `main`.

## 5. What does not exist yet

- Deployment on Unraid: nothing is running; the compose folder does not exist on the server
  (see `02-open-questions.md` Q1). NPM would return 502 today.
- Forwarded headers, host filtering, auth, roles, data protection keys (M1).
- EF Core model, migrations, importer, revisions (M2).
- Council CLI (M3), reviewer gate backed by the API (M4), FSRS (M5), real sessions (M6),
  day status / streak / skip (M7), Web Push (M8), backups (M10).
- Content lint in CI (`tools/content-lint` README shows the YAML step; not in `ci.yml`).
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
3. **Test count.** Reports say 7; the repo has 8 (see §4).
4. **Cache headers.** `runs/13-ui.md` says no-cache headers for `sw.js`/manifest are missing;
   `Program.cs` now has them (added after report 13, no report). `index.html` is still not
   no-cache and the manifest MIME type is unverified.
5. **Bare `/api`.** `runs/07-m0-fixes.md` saw 404; `runs/09-m0-final-review.md` says bare `/api`
   falls through to the SPA. Verify with a test in M1; desired behaviour is 404.
6. **Compose folder spelling.** `README.md` says `/mnt/user/docker/palablras/`, `AGENTS.md` says
   `/mnt/user/docker/palabras/`. Neither exists on the server. Recommendation: `palabras`
   (`02-open-questions.md` Q1).
7. **README is stale.** It still describes a placeholder screen, deferred manifest and the old
   milestone numbering (M7 = service worker, M8 = backups). Use ΔG numbering (§7). README is
   owned by other agents; do not edit it from a docs-only task.

## 7. Milestones (ΔG numbering)

MVP = M0–M10. Later = L1–L6. Full text in `design-amendment.md` ΔG.

| ID | Scope | State |
| --- | --- | --- |
| M0 | Repo, CI, deploy hello | Done except real Unraid deploy |
| M1 | Auth + roles + edge | Next |
| M2 | Content schema v2 + lifecycle + importer + lint + revisions | Schema + lint + 58 words exist; no DB |
| M3 | Council CLI (`tools/council`) | Not started |
| M4 | Reviewer gate `/revisar` | Mock UI only |
| M5 | FSRS-6 + cards created on approval (parallel after M2) | Not started |
| M6 | Session composer + exercises 1–4 + offline | Mock UI only |
| M7 | Day status, streak, rolling skip | Not started |
| M8 | Web Push at 22:00 | Not started |
| M9 | Wide-tablet learner layout | Mock layout done; device check pending |
| M10 | Backups + restore | Not started |
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
