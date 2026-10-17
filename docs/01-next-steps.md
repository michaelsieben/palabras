# Next steps

Ordered queue. Take items top to bottom; each has a "done when" test. Milestone IDs follow
`design-amendment.md` ΔG. MVP = M0–M10. Context and constraints: `00-handoff.md`.

## 0. M0: first real deploy (MVP) — done

Done in T-20261017-1516. Kept for reference; every later item assumes a running container behind NPM.

- Decide the compose folder name (`02-open-questions.md` Q1, recommended `palabras`).
- On Unraid: clone to `/mnt/user/repos/palabras`, create `/mnt/cache/appdata/palabras`,
  `chown 1654:1654` it, set `APPDATA_PATH` in `.env` (blank in `.env.example`), and run compose
  from `deploy/unraid/`.
- In NPM: proxy host `palabras.siebentower.nl` → `http://palabras:18480`, SSL on.

**Done when:** `curl -fsS http://127.0.0.1:18480/healthz` on the host and
`https://palabras.siebentower.nl/healthz` from the LAN both return 200, and the mock UI loads
on the OnePlus 15.

## 1. M1 — auth, roles, edge (MVP) — done

Done in T-20261017-1516 (`b4fa213` SQLite cookie auth, `0cb23fa` login UI; migration `InitialAccounts`). Kept for
reference; the ordering in `00-handoff.md` §8 still describes the shipped edge.

- `UseForwardedHeaders` first; `KnownIPNetworks` = `172.18.0.0/16`; no HTTPS redirection or HSTS.
- `AllowedHosts` = `palabras.siebentower.nl;localhost;127.0.0.1`.
- Cookie auth `__Host-palabras` (`SecurePolicy=Always`), data protection keys in `/data/keys`.
- Accounts are created by the CLI `user create <role> --username <name>` (renamed later with
  `user set-username <old> --username <new>`, password via `user set-password`) in the running container, never
  seeded from `.env`: follow `03-auth-bootstrap.md` (supersedes `design.md` `:212-213`).
  PBKDF2-SHA256 hashes; stored roles `owner` and `reviewer` (owner also authorizes as learner);
  `X-Palabras: 1` CSRF header. See `04-accounts.md` and `05-auth.md` for shipped contracts.
- `/login` in the SPA; the mock routes are gated by role (`/revisar`, `/aprobadas` reviewer-only).
- Fix the edge items from `runs/09-m0-final-review.md` and `runs/10-m0-shouldfix.md` that are
  still open, including authenticated bare `/api` → 404 (anonymous → 401) and `index.html` no-cache.

**Done when:** integration tests cover forwarded `X-Forwarded-Proto: https` from a `172.18.x.x`
peer (cookie issued) and from an untrusted peer (ignored); unknown `Host` → 400; non-GET without
`X-Palabras` → 400/403; reviewer-only route → 403 for the learner; and login over
`https://palabras.siebentower.nl` survives a container rebuild (keys persisted). Per
`03-auth-bootstrap.md` §7, a fresh database has no usable account until `user create` has been run, and an
unauthenticated request to a non-public route returns 401/redirect instead of rendering the app.

## 2. M2 — data model and EF Core migrations (MVP)

- SQLite in WAL mode at `/data/palabras.db`; EF Core model for content (words, sentences,
  translations, alternatives) with lifecycle `draft / pending_review / approved / needs_change /
  rejected / retired`, revisions, `suspended_reason` and `epoch` (ΔD).
- Importer: `content/**/*.json` → DB as `pending_review`, idempotent (matches `design-amendment.md` ΔE.2.2, locked
  2026-10-18). Owner-only CLI `content approve <key…|--all-pending>` ships here, ahead of M4, so approved words (and
  later their cards) exist before the reviewer UI; it writes the same revision row the M4 UI will write.
- Wire `tools/content-lint` into `.github/workflows/ci.yml` (step text in its README).
- ~~Fix the schema enum typo~~ Done in T-20261017-1516: the misspelt scope `"paninsular-generic"`
  (`content/schema/word.schema.json:68`, also used by the word files) is now `"peninsular-generic"`
  everywhere; see `02-open-questions.md` Q9.

**Done when:** `dotnet ef database update` creates the schema from zero; re-running the importer
on the 58 words changes nothing; CI fails on a deliberately broken word file; migration applies
on startup in the container without manual steps.

## 3. M5 — SRS (FSRS-6) (MVP)

Can run in parallel with M3/M4 once M2 is in.

- FSRS-6 from the Fsrs.Sharp 2.0.0 NuGet package (pinned `[2.0.0]`) behind an `IScheduler` seam, with a
  vendored-source fallback (MIT source in `Backend/Palabras/Srs/Vendor/`) if the package cannot take an explicit
  review time. Default 21 weights, 4 grades (Again/Hard/Good/Easy), desired retention 0.90, 12 new cards/day,
  `max_interval_days` 730. Cards are created only when content is approved (ΔD).
- Golden-vector tests: `tools/fsrs-golden/` (Python, py-fsrs 6.3.2, seeded; created in step S4 of T-20261018-0124)
  writes `Backend/Palabras.Tests/Srs/golden.json`, which is committed so CI needs no Python.

**Done when:** the `IScheduler` implementation matches the py-fsrs golden vectors (stability and
difficulty within 1e-6, due equal to the second, fuzz off); approving a word creates its cards, retiring it
suspends them.

## 4. M6 — session and exercise UI (MVP)

- Session composer: `session_target_s = 600`, hard cap `session_max_s = 900`; due reviews
  first, new cards (at most 12/day) only while the estimate stays under target (`design.md` budget rule).
- Exercises 1–4 from `design.md`, replacing `Frontend/src/mock/demo.ts` with API calls. Feedback shows a Spanish
  verdict with an English explanation; accent-only misses are accepted unless the card sets `accentsAreThePoint`;
  the slow-answer thresholds (12 s typed, 8 s recognition reveal) are provisional named constants in one place.
- Offline: IndexedDB queue for answers made without network, replayed on reconnect.
- Then M7: day status, streak and the rolling skip (1 per 7 days, refunded on same-day
  completion, ΔC). Then M9: confirm the tablet layout on the real OnePlus Tab 3.

**Done when:** a learner completes a session end to end on the OnePlus 15 with only approved
content; reviews written offline are applied after reconnect without duplicates; skip rules are
covered by unit tests at the day boundary in the user's timezone (fallback `Europe/Madrid`, ΔC.4).

## 5. M3 + M4 — reviewer view (MVP)

Content needs approval before M6 is useful, so start M4 right after M2 if possible.

- M4: `/revisar` backed by the API: pick one of the pre-generated alternatives, edit, mark
  `needs_change`, reject. Her decision is final; every change writes a revision.
- M3: council CLI in `tools/council` (offline, Vercel AI SDK, privacy guard ΔH) that produces
  alternatives as `pending_review` files for the importer.

**Done when:** the reviewer approves a word on her phone and the learner sees it in the next
session; a rejected word never reaches the learner (test the approved-only gate); a council run
produces lint-clean files.

## 6. M8 — reminders and Web Push (MVP)

- VAPID keys in `/data`, push subscription per device, `BackgroundService` sends at 22:00
  in the user's timezone when the day is not done or skipped.
- Notification actions `Saltar hoy` and `Silenciar` (ΔB/ΔC); the service worker handles them.

**Done when:** on the OnePlus 15 with the PWA installed, the 22:00 notification arrives; `Saltar
hoy` records a skip (200) or reports no skip left (409); no push is sent on a completed or
skipped day.

## 7. M10 — backups and restore (MVP)

**Done when:** `BackupService` writes the nightly 03:30 `VACUUM INTO
'/data/backups/palabras-yyyyMMdd.db'` and prunes to `BACKUP_RETENTION_DAYS` (`design.md`), and
the documented restore (copy over `/data/palabras.db`, delete `-wal`/`-shm`) into a fresh
container passes the M1 login test.

## 8. Council tooling and other later work (L1–L6)

- L2 council report/rework loop, L1 listening exercises, L3 stats, L4 FSRS optimiser,
  L5 passkeys, L6 ICS/webhook reminder fallback. Listening is the only later learner exercise; pronunciation
  scoring and sentence mining are dropped from the roadmap (locked 2026-10-18).

**Done when:** defined per item when it is picked up; not in MVP.

## Housekeeping (any time, owned by whoever touches the file)

- Refresh `README.md`: mock UI exists, manifest/service worker exist, ΔG milestone numbers,
  compose folder spelling.
- Confirm the 8th backend test passes and record it in a report.
- Measure real CSS viewports on both devices (`runs/13-pwa-ultrawide.md`).
