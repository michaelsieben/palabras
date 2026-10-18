# T-20261017-1516 / Step 01 — "Palabras" Spanish-vocab PWA design

Design only. No repo, no code, no scaffold created.

## 0. Grounding (facts vs assumptions)

Facts (cited, from the workspace `D:\Visual Studio Repo\Personal`):
- The user already has a 2-user personal app with the exact shape this product needs: Budgeteer. Its stack is React 19 + TS + Vite 8 + Tailwind v4 (`Budgeteer/AGENTS.md:12`), with SQLite via EF Core (`Budgeteer/AGENTS.md:14`) and migrations on startup (`Budgeteer/AGENTS.md:66`).
- Budgeteer builds as a single image: Node build → .NET 10 SDK publish → SPA copied into `wwwroot` → `aspnet:10.0` runtime on :8080, with SQLite at `/data` (`Budgeteer/Dockerfile:2`, `:13`, `:23`, `:26`, `:32-33`).
- Budgeteer's compose has one service `app`, maps host port `8080:8080`, and keeps the secret in env `${JWT_KEY}` with a named volume `budgeteer-data` (`Budgeteer/docker-compose.yml:2`, `:7`, `:10`, `:15`, `:18-19`).
- Budgeteer stores its JWT in `localStorage` (`Budgeteer/AGENTS.md:75`) and has open `/register` (`Budgeteer/AGENTS.md:105`). Both are deliberately **not** copied here (see §5).
- The local compose repo `nas-docker-compose/*` uses `PUID=1026` everywhere (e.g. `nas-docker-compose/radarr/docker-compose.yml:9`). No `traefik|caddy|swag|nginx|networks:|external: true|/mnt/user/appdata` matched in that tree or in `Personal/Docker` (grep, 0 hits). So **the local files do not show which reverse proxy runs on Unraid.** `PUID=1026` looks like an old Synology setup, not Unraid (Unraid default is 99:100). UNVERIFIED: whether those compose files match what runs today.
- `nas-docker-compose/tailscale/docker-compose.yml` exists but is empty (0 lines).
- A `homeassistant` folder exists (`nas-docker-compose/homeassistant/docker-compose.yml`), which opens an optional notification fallback (§6). UNVERIFIED: that HA is running now.

Environment checks: the shell tool was denied for this subagent, so `node -v`, `docker --version`, `dotnet --list-sdks` and `gh auth status` were **not run**. UNVERIFIED: local toolchain versions and gh auth. The implementer must run them in M0.

Assumptions:
- A1. Two users only: owner (learner + admin) and fiancée (reviewer). No third user, ever.
- A2. ~~Owner's timezone is Europe/Amsterdam (UNVERIFIED; it is a per-user setting, so nothing hard-codes it).~~ Superseded by `design-amendment.md` ΔC.4/ΔF: per-user timezone captured from the device at first login, fallback `Europe/Madrid`; container `TZ` defaults to `Europe/Madrid` (ΔA).
- A3. Phones: unknown OS for either user. The design covers both and includes a fallback.

## 1. Stack recommendation

**Recommended: the "Budgeteer shape".** React 19 + TS + Vite + `vite-plugin-pwa` (Workbox) SPA, served by an ASP.NET Core (.NET 10) minimal-API backend in **one container**, with SQLite via EF Core, cookie auth, an in-process `BackgroundService` scheduler and Web Push.
- Trade-off: it reuses a proven Dockerfile, compose and conventions the user already ships (`Budgeteer/Dockerfile:1-38`). ~~The cost is porting FSRS-6 to C# (about 150 LOC), which golden vectors from the official `ts-fsrs` guard (§3).~~ Superseded (T-20261018-0124): FSRS-6 comes from the Fsrs.Sharp 2.0.0 package behind `IScheduler`, gated by py-fsrs 6.3.2 golden vectors (§3).

**Fallback: all-TypeScript.** Same React PWA plus a Hono (Node 22) API, Drizzle + better-sqlite3, the official `ts-fsrs` lib, and `web-push` npm.
- Trade-off: one language and an official FSRS library with no port, but the backend drops the user's .NET/EF/Budgeteer conventions and DI/testing habits.

Layers:
| Concern | Choice |
|---|---|
| Frontend | React 19, TS strict, Vite, Tailwind v4, TanStack Query, `vite-plugin-pwa` (injectManifest, custom SW for push), IndexedDB via `idb` for offline answer queue |
| Backend | ASP.NET Core .NET 10 minimal APIs, `internal sealed` services, CancellationToken everywhere |
| DB | SQLite (WAL mode) on `/data/palabras.db`. No separate DB container, because one writer plus two users needs no Postgres |
| Auth | ASP.NET Core cookie authentication plus `PasswordHasher<T>` (PBKDF2, from `Microsoft.Extensions.Identity.Core`). No full Identity UI, no JWT |
| Scheduler | One `BackgroundService` with `PeriodicTimer(1 min)`. No Hangfire or Quartz |
| Push | Web Push with VAPID. Library: `Lib.Net.Http.WebPush` or `WebPush` NuGet (UNVERIFIED: maintenance status; pick the one with a release in the last 12 months during M7) |
| Hosting | One container behind the user's existing reverse proxy. TLS terminates at the proxy |

**TS/.NET boundary:** TypeScript owns everything in the browser: UI, service worker, offline queue, answer checking for typed input (with the same normalisation also on the server), TTS and session *execution*. .NET owns persistence, auth, FSRS scheduling (server-authoritative), session *composition*, streak/skip rules, push sending, backups and content import. Shared contract: the C# records are the source of truth, and TS types are hand-written in `Frontend/src/types` (about 15 types, so a codegen dependency is not worth it).

Size check: 1 container, 1 file DB, 0 queues, 0 external services at runtime. No k8s, no microservices, no Redis, no LLM at runtime.

## 2. Data model (SQLite, EF Core)

All timestamps are `DateTimeOffset` UTC. `local_date` is a `TEXT yyyy-MM-dd` in the user's timezone. Ids are `INTEGER` autoincrement, except `client_attempt_id` (UUID text).

```
users
  id PK, username UNIQUE NOT NULL, display_name, role TEXT CHECK(role IN ('owner','reviewer')),
  password_hash, failed_login_count INT DEFAULT 0, locked_until NULL, timezone TEXT NULL (ΔF: was DEFAULT 'Europe/Amsterdam'; set from device, fallback 'Europe/Madrid'),
  created_at
user_settings                        -- learner only (1:1 users)
  user_id PK FK users, desired_retention REAL DEFAULT 0.90, new_per_day INT DEFAULT 12,
  max_reviews_per_day INT DEFAULT 60, session_target_s INT DEFAULT 600, session_max_s INT DEFAULT 900,
  reminder_time TEXT DEFAULT '19:00', last_call_time TEXT DEFAULT '21:30',
  fsrs_weights TEXT(JSON array[21]), fsrs_weights_version INT DEFAULT 1, max_interval_days INT DEFAULT 730,
  learning_steps TEXT DEFAULT '["1m","10m"]', relearning_steps TEXT DEFAULT '["10m"]'

topics           id PK, key UNIQUE ('food','transport','home','fiestas-navarra',...), name_es, name_en
tags             id PK, key UNIQUE
word_tags        word_id FK, tag_id FK, PK(word_id, tag_id)

words
  id PK, key TEXT UNIQUE (stable slug, e.g. 'patata-n'), lemma TEXT NOT NULL, article TEXT NULL ('el','la','los','las'),
  gender TEXT NULL CHECK IN ('m','f','mf'), pos TEXT CHECK IN ('noun','verb','adj','adv','phrase','prep','pron','conj','interj'),
  cefr TEXT CHECK IN ('A1','A2','B1'), topic_id FK topics,
  region TEXT CHECK IN ('ES','ES-NA') DEFAULT 'ES',      -- ES = peninsular/Castilian, ES-NA = Navarra/Pamplona-specific
  register TEXT CHECK IN ('neutral','colloquial','formal') DEFAULT 'neutral',
  forms TEXT JSON NULL  -- {plural, fem, vosotros_present, preterite_1s, irregular:true}
  latam_equivalent TEXT NULL,   -- e.g. 'carro' for 'coche' (display-only "not this")
  regional_note TEXT NULL, pronunciation_note TEXT NULL (e.g. 'z = /θ/ (distinción)'),
  status TEXT CHECK IN ('draft','pending_review','approved','needs_change','rejected','retired'),
  version INT DEFAULT 1, source TEXT ('seed-llm','manual','reviewer'), frequency_rank INT NULL,
  created_at, updated_at
  IDX words(status), IDX words(topic_id, cefr), UNIQUE(lemma, pos)

senses                               -- 1 word : N EN glosses / meanings
  id PK, word_id FK words ON DELETE CASCADE, sense_order INT, en_gloss TEXT NOT NULL,
  en_accepted TEXT JSON  -- extra accepted EN answers ['potato','spud']
  es_accepted TEXT JSON  -- accepted ES variants for typed answers (without article etc.)
  example_es TEXT, example_en TEXT, regional_note TEXT NULL, is_primary BOOL
  UNIQUE(word_id, sense_order)

word_alternatives                    -- pre-generated candidates, stored with the word at import
  id PK, word_id FK, sense_id FK NULL, field TEXT CHECK IN ('lemma','gloss','example','regional_note'),
  proposed_es TEXT NULL, proposed_en TEXT NULL, rationale TEXT, rank INT,
  source TEXT ('seed-llm','reviewer','manual'), created_at
  IDX(word_id, field, rank)

cards                                -- 1 per (sense, direction, learner)
  id PK, sense_id FK senses ON DELETE CASCADE, user_id FK users,
  direction TEXT CHECK IN ('es_en','en_es'),
  state INT (0 New,1 Learning,2 Review,3 Relearning), due DATETIMEOFFSET, stability REAL, difficulty REAL,
  elapsed_days INT, scheduled_days INT, learning_step INT, reps INT, lapses INT, last_review NULL,
  suspended BOOL DEFAULT 0, introduced_at NULL
  UNIQUE(sense_id, user_id, direction)
  IDX cards(user_id, suspended, due)            -- due queue
  IDX cards(user_id, state)                     -- new queue

practice_sessions
  id PK, user_id FK, local_date, started_at, completed_at NULL, duration_s INT, planned_s INT,
  new_count, review_count, status CHECK IN ('in_progress','completed','abandoned')
  IDX(user_id, local_date)

review_log                           -- append-only, the FSRS optimiser training set
  id PK, client_attempt_id TEXT UNIQUE (idempotent offline sync), card_id FK, session_id FK,
  exercise_type TEXT, rating INT (1 Again,2 Hard,3 Good,4 Easy), correct BOOL, response_ms INT,
  answer_text TEXT NULL, reviewed_at (client clock, clamped to server ±24h),
  state_before INT, stability_before REAL, difficulty_before REAL,
  stability_after REAL, difficulty_after REAL, elapsed_days INT, scheduled_days INT,
  fsrs_weights_version INT, counts_for_srs BOOL   -- false for warm-up/match-game exposures
  IDX(card_id, reviewed_at), IDX(session_id)

day_status
  user_id FK, local_date, iso_week TEXT ('2026-W42'),
  status CHECK IN ('done','skipped','frozen','missed'), session_id NULL, decided_at
  PK(user_id, local_date), IDX(user_id, iso_week)

push_subscriptions
  id PK, user_id FK, endpoint TEXT UNIQUE, p256dh, auth, user_agent, created_at,
  last_success_at NULL, failure_count INT DEFAULT 0
reminder_log
  id PK, user_id, local_date, kind ('daily','last_call'), channel ('webpush','webhook'), sent_at, result
  UNIQUE(user_id, local_date, kind, channel)   -- never double-send

word_reviews                         -- fiancée feedback, append-only
  id PK, word_id FK, word_version INT, sense_id FK NULL, reviewer_id FK users,
  verdict CHECK IN ('approve','reject','needs_change'),
  field CHECK IN ('lemma','gloss','example','regional_note','whole') NULL,
  chosen_alternative_id FK word_alternatives NULL, proposed_text TEXT NULL, note TEXT NULL,
  created_at, resolution CHECK IN ('open','applied','dismissed') DEFAULT 'open', resolved_at NULL
  IDX(word_id, word_version), IDX(resolution)
```

Relations: words 1–N senses 1–N cards. words 1–N word_alternatives. words 1–N word_reviews. cards 1–N review_log. Review queue query: `words WHERE status='pending_review' AND NOT EXISTS(word_reviews for current version)`, ordered by `topic, frequency_rank`.

Plus EF's `__EFMigrationsHistory`. Data Protection keys are stored as files in `/data/keys`, not in the DB.

## 3. SRS algorithm: FSRS-6

**Algorithm:** FSRS-6 (21 weights, `w0..w20`), server-authoritative, ~~ported to C# from `ts-fsrs` v5.x (open-spaced-repetition)~~ provided by the Fsrs.Sharp 2.0.0 NuGet package (pinned `[2.0.0]`) behind an `IScheduler` seam, with a vendored-source fallback (T-20261018-0124). The formulas below describe FSRS-6; the library supplies the default weights and fuzz.
- Default weights: ~~copy them verbatim from `ts-fsrs` `default_w` at implementation time~~ use the Fsrs.Sharp 2.0.0 defaults. From memory, which is UNVERIFIED and must not be used as-is: `[0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542]`.
- Retrievability: `R(t,S) = (1 + F·t/S)^(-w20)`, with `F = 0.9^(-1/w20) - 1`.
- Next interval: `I = S/F · (r^(-1/w20) - 1)`, where `r = desired_retention`. Round it, clamp to `[1, max_interval_days]`, and apply fuzz (±, ts-fsrs fuzz ranges) so cards don't clump.
- Stability/difficulty updates, same-day short-term stability (`w17..w19`) and the difficulty mean-reversion follow ~~ts-fsrs~~ the library (validated against py-fsrs 6.3.2) exactly.
- **Correctness gate:** `tools/fsrs-golden/` (Python script, py-fsrs 6.3.2 pinned in `requirements.txt`; ~~Node script with ts-fsrs~~) generates about 500 seeded random review sequences into `Backend/Palabras.Tests/Srs/golden.json`, which is committed so CI needs no Python. A C# xUnit test replays them through the `IScheduler` implementation and asserts S/D within 1e-6 and due equal to the second with fuzz disabled.

**Storage:** weights live in `user_settings.fsrs_weights` (JSON) plus `fsrs_weights_version`, and each `review_log` row records the version used. Card memory state lives in the `cards` columns (§2).

**Short → long term mapping (what the user sees):**
| Stage | Rule | Meaning |
|---|---|---|
| Nuevo | state 0 | not yet seen |
| Aprendiendo (short-term) | state 1/3, learning steps 1m → 10m inside the session | working memory → short-term |
| Reciente | state 2, S < 21 d | consolidating |
| Consolidada (long-term) | state 2, S ≥ 21 d | long-term |
| Dominada | S ≥ 90 d | mastered |

Learning steps happen *inside the session*: a failed or new card is re-queued by the client after ≥ 3 other cards, or at session end. Steps that are not finished at session end carry over to tomorrow (due = next day) rather than keeping the session open.

**Target retention:** 0.90 (setting, 0.85–0.95).

**Session sizing (assumptions):** new card ≈ 30 s total (intro + 2 learning reps); review card ≈ 8 s (typed ≈ 12 s, flip ≈ 6 s).
- Defaults `new_per_day = 12` and `max_reviews_per_day = 60`: 12 new + 30 due reviews give 12×30 + 30×8 = 600 s, which is 10 min. With 45 due reviews the full 12 new would be 12×30 + 45×8 = 720 s, so the budget rule below trims new cards to 8 (8×30 + 45×8 = 600 s).
- **Budget rule:** session composer target is `session_target_s = 600`, with a hard cap of `session_max_s = 900`. Due reviews come first (most overdue / lowest R first). New cards are then added only while `est ≤ target`. If due reviews alone exceed 900 s, new cards drop to 0 and overflow reviews stay due (FSRS tolerates lateness).
- **Floor:** if est < 300 s, the composer pads with "refuerzo" cards (lowest R not yet due, `counts_for_srs = true`, which is legitimate early review in FSRS) up to 300 s.
- Per-type seconds are re-estimated from the rolling median of `review_log.response_ms` over the last 14 days, so sizing self-calibrates.
- Day counts as **done** when the composed session is completed, or when ≥ 300 s of active time is logged and the user ends it.

**Skip once per ISO week (Mon–Sun, user timezone):**
- *Skip* = the user taps "Saltar hoy" (in-app or from the notification action) before local midnight. It writes `day_status(skipped)`. Allowed only if no `skipped|frozen` row exists for the current `iso_week`.
- *Missed day* (no done/skip by midnight), handled by the 00:05 rollover job:
  - If this week's skip is unused, write `frozen`. It consumes the skip automatically, like a streak freeze. (Open question 4.)
  - Otherwise write `missed`, and the streak resets to 0.
- *Second skip attempt* in the same week: the button is disabled with the text "Ya usaste tu salto de esta semana". The API returns 409. A notification action reaches the API, which returns 409, and the SW shows "No skip left — 5 min?".
- *Streak* = consecutive local days with status in (`done`, `skipped`, `frozen`) and at least one `done` in each ISO week. Skip/frozen days keep the streak alive but don't increment it. Display: "Racha 12 · salto disponible ✓/✗".
- Skipping does **not** move FSRS dues. Tomorrow's session is composed normally, and the budget rule caps it.

## 4. Game/session design

Cards come in two directions: `es_en` (recognition) and `en_es` (production). The `en_es` card for a sense unlocks only after `es_en` reaches state 2, so production follows recognition.

Exercise types:
1. ~~**Presentación + Parejas (memory match)**: new cards only. A batch of up to 6 new words is shown first (ES with article, EN, example, TTS es-ES), then a 6-pair flip-match grid (ES ↔ EN). `counts_for_srs = false` (exposure). The first correct recall comes in the next exercise.~~ Superseded (owner decision, T-20261018-2105): not built; new cards start as `recognition` (`07-learn-api.md`, deviations).
2. ~~**Opción múltiple ES→EN**: Learning/Relearning cards. 4 options, distractors from the same POS and topic. Correct → Good; wrong → Again. Recognition only, so Easy is never given.~~ Superseded (owner decision, T-20261018-2105): not built; the shipped modes are `recognition`, `typed`, `cloze` only.
3. **Tarjeta ES→EN (flip, self-graded)**: Review `es_en`. Shows ES (+ TTS). User reveals and taps Again/Hard/Good/Easy: four directly visible buttons with a "forgot → Again" hint (locked 2026-10-18). ~~Default buttons are Again/Good, with Hard/Easy reachable by long-press (keeps it fast).~~
4. **Escribe EN→ES (typed production)**: Review `en_es`, and Learning `en_es`. Auto-graded after normalising (trim, lowercase, optional article):
   - exact match → Good ~~(Easy if < 4 s)~~; slower than 12 s → Hard. The 12 s typed / 8 s recognition-reveal thresholds are provisional, kept as named constants in one place and revisited after the first real sessions (O-4, T-20261018-0124);
   - ~~accent or `ñ` missing only → Hard, with diff shown;~~ accent-only miss → accepted as correct (Good) with the difference shown, unless the card sets `accentsAreThePoint` (then Again). `ñ` is not folded, so `n` for `ñ` is not an accent miss. Verdicts are Spanish, explanations English (locked 2026-10-18);
   - gender/article wrong → Hard, with the article highlighted;
   - otherwise → Again.
   - Accepted answers = `lemma` + `senses.es_accepted`.
   - A LatAm form (e.g. "carro" when `latam_equivalent='carro'`) → Hard, with the message "En España: coche".
5. **Escucha (listening)**: Review `es_en` with S ≥ 21 d, at most 1 in 5 cards. Plays TTS es-ES (Web Speech API, prefers voices with `lang === 'es-ES'`), and the user types or chooses EN. If no es-ES voice is available, this type is disabled (feature-detected).

**Selection:** by card state + direction, as listed. A small variety rule prevents more than 3 of the same type in a row.

**Session composition** (server returns an ordered plan; client executes it):
`[due reviews, interleaved, ~70%] → [new batch: Presentación+Parejas → MC] → [learning re-queue] → [refuerzo pad if under floor]`. ~~A progress bar shows the estimated minutes remaining. The session ends early with "Bien hecho" if the timer passes 900 s, and the rest remains due.~~ Superseded (owner decision, T-20261018-2105): composer budgeting replaces the estimate and the timed end; the new batch is plain `recognition` cards (`07-learn-api.md`, deviations).

**Per attempt record** (client → server, batched every 10 attempts and at session end, retried offline): `client_attempt_id, card_id, session_id, exercise_type, rating, correct, response_ms, answer_text, reviewed_at`. The server computes FSRS and fills the `*_before/_after` columns. Learning re-queue order is client-side, so a session runs fully offline after the plan is fetched. The server applies attempts in `reviewed_at` order per card.

## 5. Auth

- **Mechanism:** username + password, verified with `PasswordHasher<User>` (PBKDF2). ASP.NET Core **cookie auth**:
  - `HttpOnly`, `Secure`, `SameSite=Lax`, sliding 30-day expiry, cookie name `__Host-palabras`.
  - Cookies are used instead of Budgeteer's localStorage JWT (`Budgeteer/AGENTS.md:75`). Cookies are not readable by XSS, and service-worker fetches and notification actions send them automatically.
- **No registration.** No `/register` endpoint, which differs from Budgeteer (`Budgeteer/AGENTS.md:105`).
  - On first boot, if `users` is empty, the app creates `owner` and `reviewer` from the env `OWNER_USERNAME/OWNER_INITIAL_PASSWORD` and `REVIEWER_USERNAME/REVIEWER_INITIAL_PASSWORD`.
  - After that, the env passwords are ignored. A startup log warns if they are still set.
  - Password change happens in-app. Reset uses the CLI `docker exec palabras dotnet Palabras.dll set-password <user>` (reads from stdin).
- **Roles → views:** claim `role`. Policies:
  - `Learner` = owner; `Reviewer` = reviewer or owner (the owner can preview her view); `Admin` = owner.
  - API groups: `/api/learn/*` (Learner), `/api/review/*` (Reviewer), `/api/admin/*` (Admin: import, export, apply-feedback).
  - SPA routes: `/` learner home, `/revisar` reviewer queue. After login, each role is redirected to its home, and the reviewer never sees learner screens.
- **Security minimums:**
  - HTTPS only, terminated at the proxy, plus `UseForwardedHeaders` with `KnownNetworks` limited to the proxy's docker network. HSTS is set at the proxy.
  - `AddRateLimiter` on `POST /api/auth/login`: 5/min per IP, fixed window. Account lockout of 15 min after 10 consecutive failures (`failed_login_count`, `locked_until`).
  - CSRF protection: `SameSite=Lax` plus a required custom header `X-Palabras: 1` on all non-GET `/api` calls, rejected if missing. Browsers can't send custom headers cross-site without CORS preflight, and no CORS is configured.
  - Data Protection keys persisted to `/data/keys`. Without this, every container restart logs both users out.
  - CSP: `default-src 'self'`. No third-party scripts.
  - Login error messages are generic.
- **Later:** passkeys (UNVERIFIED: built-in passkey support in .NET 10 Identity; check before adopting). Not MVP.

## 6. Reminders

**The real constraint:** Web Push needs a service worker, VAPID keys and HTTPS with a valid cert.
- **iOS/iPadOS:** only on 16.4+, and only when the PWA is **added to the Home Screen** and permission is requested from a user gesture inside the installed app. Notification action buttons are not reliably shown on iOS (UNVERIFIED; treat as absent), so the "Saltar" action degrades to opening the app.
- **Android Chrome:** works from the browser or the installed PWA, and supports actions.
- Push is best-effort on both. Subscriptions expire, and the OS may delay delivery.

**Sender:** `ReminderService : BackgroundService`, `PeriodicTimer(1 min)`. For the learner:
1. Compute the user's local time and `local_date`.
2. If local time ≥ `reminder_time`, there is no `day_status` for today, there is no `reminder_log(daily)` for today, and no session is in progress → send "Tu repaso de hoy: ~N min · salto disponible ✓/✗".
3. At `last_call_time`, if still not done → send "Última llamada". The text differs depending on whether the skip is used:
   - skip available: "o salta hoy (1/semana)";
   - skip used: "sin saltos esta semana — 5 min salvan la racha".
4. Delete subscriptions that return 404/410. Increment `failure_count` on other errors.
5. `reminder_log` UNIQUE prevents duplicate sends after a restart.

**Skip interaction:** once today is skipped or done, nothing more is sent. If the skip is used, the last-call message warns that the streak is at risk. Both notifications carry `actions: [{action:'start'}, {action:'skip'}]`. The `skip` action makes the SW call `POST /api/learn/day/skip` with credentials: 200 shows "Saltado ✓", and 409 shows the "no skip left" notice.

**Honest fallback** (the device can't push, or the user declines):
1. **ICS calendar feed** (always available, zero infra): `GET /cal/<random-256-bit-token>.ics` serves a daily recurring event at `reminder_time` with a `VALARM`. It is subscribed once in the phone's calendar. The feed is static: it can't know "done", so it reminds every day. Rotate the token in settings.
2. **Optional webhook** `FALLBACK_NOTIFY_URL`: a POST with `{title, body, url}` per reminder, to self-hosted ntfy or a Home Assistant webhook automation (HA companion app push). It goes through the same logic and `reminder_log` with `channel='webhook'`.
3. In-app banner and app badge (`navigator.setAppBadge`, where supported) when the app opens and today is pending.

## 7. Fiancée feedback view + model loop

**View `/revisar`** (mobile-first, Spanish UI, Reviewer policy):
- A queue card per word (current version): lemma with article, gender, POS, primary gloss + other senses, example ES/EN, regional note, the "En LatAm: …" contrast, TTS button. Counter: "12 pendientes".
- Three big buttons: **Vale ✓ (approve)**, **No ✗ (reject)**, **Cambiar ✎ (needs_change)**.
  - **Approve**: `word_reviews(approve)` → `words.status = approved`. Next card.
  - **Reject or Cambiar**: a sheet opens instantly (offline-safe, data already prefetched) with the field selector (palabra / traducción / ejemplo / nota) and the **pre-generated alternatives** for that field from `word_alternatives`, ranked, as tappable chips. Plus a free-text "Otra:" input and an optional note.
    - Tapping a chip records `chosen_alternative_id`, and the server **auto-applies** it: the field is updated, `version++`, `status = approved` (her choice is the approval), cards for changed senses are **reset to New** only when lemma or gloss changed (an example/note change keeps SRS state), and `resolution = applied`.
    - Free text records `proposed_text` with `status = needs_change`, `resolution = open`. The owner applies it in the admin view (one tap "Aplicar") → `version++`, `status = pending_review`, so she sees it again for a final OK.
    - A reject with no alternative and no text → `status = rejected`, cards suspended.
- The reviewer queue is cached offline. Verdicts queue in IndexedDB and sync like review attempts (`client_review_id` idempotency; add a UNIQUE column `client_review_id` to `word_reviews`).
- ~~**Non-stalling rule:** learner new cards come from `approved` first. If fewer than `new_per_day` approved words are unseen, `pending_review` words fill the gap with a "sin verificar" badge. A later reject suspends them, and a chosen alternative swaps them in place (reset to New). Her backlog therefore never blocks his daily session.~~ Superseded by `design-amendment.md` ΔE.1 (approved-only gate): removed, not built. Before M4, the owner `content approve` CLI (M2) supplies approved words.

**Captured events (model training signal):** every `word_reviews` row, plus derived signals from the learner's `review_log`: per-word lapse rate, and typed answers matching `latam_equivalent`.

**Export record** (`GET /api/admin/feedback/export.jsonl?since=…`, owner only; also the CLI `export-feedback`). One line per review:
```json
{"schema":"palabras.feedback.v1","word_key":"patata-n","word_version":2,"lemma":"patata","pos":"noun",
 "field":"example","verdict":"needs_change","chosen_alternative":null,
 "proposed_text":"Me pones una ración de patatas bravas.","note":"más natural en un bar",
 "alternatives_offered":["…","…"],"reviewer":"native_es_navarra","created_at":"2026-10-20T19:02:11Z"}
```
`reviewer` is a role label, never a name or username. No user ids, emails or IPs.

**Model consumption (v1 = manual, offline, owner-triggered):**
1. The owner exports the JSONL.
2. The owner runs a prompt locally (Claude/Codex/opencode session) with `content/style-guide.md` + the JSONL + `content/schema/word.schema.json`. Two outputs:
   - (a) an updated **style-guide.md**: her distilled preferences, e.g. "prefer *vale* over *de acuerdo* in examples", "bar register for food examples", "almuerzo = mid-morning in Navarra";
   - (b) a new import batch (`content/words/*.json`) with regenerated alternatives for open items and new words that follow the style guide.
3. `import` validates (schema + LatAm denylist lint, §8) and upserts.

The app never calls an LLM. **Privacy:** her free-text `note` may contain personal remarks, so the export offers `?includeNotes=false`, and the owner reviews the file before pasting it into any external model. v2 (later, optional) could add a scheduled job; keep it out of v1.

## 8. Content seeding

**Format:** JSON files per topic in `content/words/<topic>.json`, validated against `content/schema/word.schema.json`. One entry:
```json
{"key":"coche-n","lemma":"coche","article":"el","gender":"m","pos":"noun","cefr":"A1","topic":"transport",
 "region":"ES","register":"neutral","latam_equivalent":"carro / auto",
 "pronunciation_note":null,"forms":{"plural":"coches"},
 "senses":[{"en_gloss":"car","en_accepted":["automobile"],"es_accepted":[],
   "example_es":"¿Vosotros venís en coche a Pamplona?","example_en":"Are you (all) coming to Pamplona by car?"}],
 "alternatives":[{"field":"example","es":"Aparcamos el coche cerca de la Plaza del Castillo.","en":"We parked the car near the Plaza del Castillo.","rationale":"local landmark"},
                 {"field":"lemma","es":"automóvil","rationale":"formal synonym"}],
 "tags":["daily-life"]}
```
**Importer:** CLI `dotnet Palabras.dll import /content` (and an admin upload endpoint later). It is idempotent upsert by `key`.
- A changed content hash → `version++` and `status = pending_review`.
- New entries → `pending_review`.
- Entries are never deleted. Entries missing from the files → `retired`, cards suspended.

**Source strategy:**
- Lexical scope: the Instituto Cervantes *Plan Curricular* A1–A2 "Nociones específicas" topic inventory (a peninsular-Spanish reference; UNVERIFIED: exact current URL). Use it only as a topic and word checklist, not copied text.
- An LLM drafts entries offline in batches of about 50, following `content/style-guide.md`. Each entry gets ≥ 3 alternatives: ≥ 2 example sentences and 1 gloss or lemma alternative, generated **at authoring time** so rejection in her view is instant and offline.
- She validates via `/revisar`.
- Target seed size: about 400 words (12/day ≈ 33 days of new material; was 8/day ≈ 50 days). Topic order: daily life, food & bar, home, transport, time/weather, fiestas, Navarra.

**Spain-not-LatAm enforcement:**
- `region ∈ {ES, ES-NA}` is required. There is no LatAm region value at all, and `latam_equivalent` exists only as a contrast to display and to grade "Hard" on.
- Content lint (`tools/content-lint`, runs in CI) denylists LatAm-only lexicon in `lemma`/`example_es` unless it is in `latam_equivalent`: carro, computadora, papa (=potato), celular, jugo, manejar (=drive), boleto, departamento (=flat), durazno, frutilla, pileta, lentes (=gafas), camión (=bus), cuadra, platicar, ahorita, chamba, chévere, plata (=money), recámara, regadera, elote. Each hit is a warning that must be overridden with `"lint_ignore":["…"]` plus a reason.
- Morphology: verb examples use **vosotros** forms at least once per verb. `forms.vosotros_present` is required for verbs. The lint rejects `ustedes` used as an informal plural unless the entry is tagged `formal`.
- Pronunciation: `pronunciation_note` for c/z (distinción /θ/), and TTS forces an `es-ES` voice.
- Navarra/Pamplona flavour, `region = ES-NA`, curated in a `fiestas-navarra` / `comida-navarra` topic: e.g. *almuerzo* (mid-morning meal), *txistorra*, *pacharán*, *pochas*, *sanfermines*, *encierro*, *peña*, *kalimotxo*, *agur*/*aupa* (colloquial, Basque-origin, tagged). UNVERIFIED: these are my examples; her review is the authority.

## 9. Deployment

**Verify the proxy first** (read-only, using the allowed `docker ps` / `network ls` verbs; show the command before running, per global rules):
```
ssh unraid-docker 'docker ps --format "{{.Names}}\t{{.Image}}\t{{.Ports}}"'
ssh unraid-docker 'docker network ls'
```
Identify by image:
| Image | Integration |
|---|---|
| `jc21/nginx-proxy-manager` | NPM UI: Proxy Host `palabras.<domain>` → `palabras:8080`, SSL Let's Encrypt, Force SSL + HSTS + HTTP/2 |
| `lscr.io/linuxserver/swag` | `proxy-confs/palabras.subdomain.conf` from the sample template, upstream `palabras:8080` |
| `traefik` | Labels on the service (router rule `Host(`palabras.<domain>`)`, `entrypoints=websecure`, `tls.certresolver=<existing>`) |
| `caddy` / `caddy-docker-proxy` | Caddyfile `palabras.<domain> { reverse_proxy palabras:8080 }` or labels |
| `cloudflare/cloudflared` | Add public hostname → `http://palabras:8080` (TLS at Cloudflare) |
| none found | Fallback: ship `compose.caddy.yml` overlay with Caddy on 80/443 (only if 80/443 free) |

Then `docker network inspect <proxy-network>` gives the name to join as `external: true`.

**docker-compose.yml shape:**
```yaml
services:
  palabras:
    image: ghcr.io/<gh-user>/palabras:${PALABRAS_TAG:-latest}   # or build: . for local
    container_name: palabras
    restart: unless-stopped
    user: "99:100"                         # Unraid nobody:users, owns appdata
    env_file: .env
    environment:
      - ASPNETCORE_URLS=http://+:8080
      - ConnectionStrings__Default=Data Source=/data/palabras.db
      - TZ=${TZ:-Europe/Madrid}              # was Europe/Amsterdam; container clock only (design-amendment.md ΔA)
    volumes:
      - ${APPDATA:-/mnt/user/appdata/palabras}:/data      # db, backups/, keys/
    networks: [proxy]
    # ports: ["${APP_PORT:-18480}:8080"]   # ONLY if no proxy network; pick a free port (check `docker ps`)
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 20s
networks:
  proxy:
    external: true
    name: ${PROXY_NETWORK:?set to existing proxy network}
```
- No `db` service: SQLite is file-based.
- A bind mount to `/mnt/user/appdata/palabras` is chosen over a named volume. That is the Unraid convention, and it is covered by the CA Appdata Backup plugin. Local dev uses `compose.override.yml` with a named volume and `ports: 8080:8080`, like Budgeteer.
- The Dockerfile copies Budgeteer's 3-stage shape (`Budgeteer/Dockerfile:1-38`) plus `apt-get install -y --no-install-recommends curl` in runtime (for the healthcheck).
- `/healthz` checks `SELECT 1` and a writable `/data`.
- No host port by default, which avoids conflicts with about 45 containers.

**Env surface (`.env.example`, no secrets committed):** `TZ`, `APPDATA`, `PROXY_NETWORK`, `PALABRAS_TAG`, `APP_BASE_URL=https://palabras.<domain>`, `OWNER_USERNAME`, `OWNER_INITIAL_PASSWORD`, `REVIEWER_USERNAME`, `REVIEWER_INITIAL_PASSWORD`, `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT=mailto:<owner>`, `FALLBACK_NOTIFY_URL=` (optional), `BACKUP_RETENTION_DAYS=14`, `FORWARDED_KNOWN_NETWORK=172.18.0.0/16`. VAPID keys are generated once with the CLI `dotnet Palabras.dll gen-vapid`. The `.env` lives only on the server at `/mnt/user/appdata/palabras/.env` or next to the compose file, never in git.

**Backup/restore:**
- `BackupService` runs nightly at 03:30 local with `VACUUM INTO '/data/backups/palabras-yyyyMMdd.db'`. That is online and consistent with WAL. It keeps `BACKUP_RETENTION_DAYS` copies.
- The Unraid Appdata Backup plugin also covers `/mnt/user/appdata/palabras`.
- Optional: copy `backups/` to Nextcloud or another share.

**Restore:**
1. `docker compose stop palabras`.
2. Copy the chosen backup over `/data/palabras.db`.
3. Delete `palabras.db-wal` and `palabras.db-shm`.
4. `docker compose start palabras`.

The restore drill is part of M8.

**DNS/TLS steps:**
1. Find the existing public entry (router port-forward 443 → proxy, or Cloudflare tunnel) from the proxy check above.
2. DNS: add `palabras.<domain>`.
   - Port-forward setup: an `A` record to the WAN IP, or a `CNAME` to the existing DDNS hostname.
   - Tunnel setup: a tunnel public hostname instead.
3. Add the proxy host/route as in the table. Certificate via the proxy's existing ACME (HTTP-01 works when 80 is forwarded; else DNS-01 with the existing provider token).
4. Verify:
   - `curl -I https://palabras.<domain>/healthz` → 200 with a valid cert;
   - `/manifest.webmanifest` is served;
   - the Chrome DevTools "Application → Manifest" shows installable.

**PWA install/offline:**
- Manifest `display: standalone`, `start_url: /`, 192/512 + maskable icons, `theme_color`.
- The SW precaches the app shell (Workbox) and runtime-caches `GET /api/learn/session/plan` (the learner session plan, M6; this is what offline learning depends on) and, for the reviewer, `/api/review/queue` (the reviewer's content queue, M4; not the learner SRS queue) (NetworkFirst, 3 s timeout).
- Attempts and verdicts queue in IndexedDB and flush on `online`, on app focus, or via Background Sync where available. There is no reliance on Background Sync on iOS.
- iOS: install via Safari Share → Add to Home Screen, then enable notifications from in-app settings.

## 10. Delivery plan — superseded by `design-amendment.md` ΔG (table struck; ΔG numbering is authoritative)

| # | Milestone | Done when | MVP |
|---|---|---|---|
| ~~M0~~ | ~~Repo + skeleton + CI + deploy hello~~ | ~~Private repo exists; CI green; `https://palabras.<domain>/healthz` 200 via the existing proxy; manifest installable~~ | ~~✓~~ |
| ~~M1~~ | ~~Auth + roles~~ | ~~Both seeded users log in; reviewer gets 403 on `/api/learn/*`; 6th login in a minute gets 429; lockout test; cookie survives restart (keys persisted)~~ | ~~✓~~ |
| ~~M2~~ | ~~Content schema + importer + lint + 150 seed words~~ | ~~Import is idempotent (2nd run = 0 changes); lint flags a planted "carro"; words visible in admin list~~ | ~~✓~~ |
| ~~M3~~ | ~~FSRS-6 port + cards + review API~~ | ~~Golden-vector test passes (500 sequences); attempts idempotent by `client_attempt_id`~~ | ~~✓~~ |
| ~~M4~~ | ~~Session composer + exercises 1–4 + offline queue~~ | ~~A 10-min session completes in airplane mode and syncs; est. duration within ±20% of actual over 3 sessions~~ | ~~✓~~ |
| ~~M5~~ | ~~Day status, streak, weekly skip, rollover job~~ | ~~Unit tests: 2nd skip → 409; missed day with skip available → frozen; Sunday/Monday ISO boundary in tz; DST day~~ | ~~✓~~ |
| ~~M6~~ | ~~Reviewer view + alternatives + auto-apply + admin apply~~ | ~~She reviews 20 words on her phone in under 5 min; chip pick applies instantly; offline verdicts sync; learner not blocked when her queue is empty~~ | ~~✓~~ |
| ~~M7~~ | ~~Web Push + ICS + optional webhook~~ | ~~Daily + last-call received on the owner's actual phone; skip action works (Android) or degrades (iOS); no duplicate after restart~~ | ~~✓~~ |
| ~~M8~~ | ~~Backups + restore drill~~ | ~~Nightly file appears; restore on a copy reproduces the session state~~ | ~~✓~~ |
| ~~L1~~ | ~~Listening exercise (TTS)~~ | ~~Feature-detected; disabled when no es-ES voice~~ | ~~later~~ |
| ~~L2~~ | ~~Feedback export + style-guide loop~~ | ~~JSONL export passes schema; one offline regeneration round imported~~ | ~~later (export endpoint can ship in M6)~~ |
| ~~L3~~ | ~~Stats (retention curve, stage counts, streak calendar)~~ | ~~—~~ | ~~later~~ |
| ~~L4~~ | ~~FSRS optimiser on own review_log (offline, `fsrs-rs`/py) once ≥ 1000 reviews~~ | ~~New weights version stored; RMSE logged~~ | ~~later~~ |
| ~~L5~~ | ~~Passkeys~~ | ~~—~~ | ~~later~~ |

**Top risks:**
1. **iOS push reliability.** It needs Home-Screen install, 16.4+ and permission, and iOS may silently drop pushes.
   - Mitigation: ICS fallback always on, optional webhook (HA/ntfy), in-app badge. Test on the actual device in M7 before calling it done.
2. **Content quality: Castilian vs LatAm, and A2 fit.** LLM drafts can leak LatAm lexicon or unnatural register.
   - Mitigation: denylist lint in CI, required `latam_equivalent`/vosotros fields, a style guide that evolves from her feedback, and her review as the gate. Seed small (150) before scaling to 400.
3. **Fiancée review queue stalls.**
   - Mitigation: ~~the non-stalling rule (pending words are learnable with a badge),~~ (removed by ΔE.1) small daily review asks (optional reviewer reminder later), chip-based instant alternatives so a review takes about 10 s per word.
- Secondary risk: an FSRS port bug (mitigated by golden vectors), and proxy unknowns (mitigated by the M0 verification step).

## 11. Repo plan

- **Name:** `palabras`, private, on the personal GitHub (`gh repo create palabras --private`; the M0 implementer confirms `gh auth status` and the account first).
- **Local folder:** `D:\Visual Studio Repo\Personal\Palabras`.
- **Single-app monorepo** (mirrors Budgeteer):
```
Palabras/
├── AGENTS.md                 # project notes (stack, conventions, run, deploy) like Budgeteer/AGENTS.md
├── README.md
├── .gitattributes            # * text=auto eol=lf
├── .gitignore  .dockerignore  .env.example
├── Dockerfile                # 3-stage (node → sdk → aspnet + curl)
├── docker-compose.yml        # prod (Unraid, external proxy network)
├── compose.override.yml      # local dev (named volume, ports 8080)
├── .github/workflows/ci.yml
├── Frontend/                 # Vite + React + TS + Tailwind + vite-plugin-pwa
│   └── src/{api,components,features/{learn,review,admin},sw,offline,types}
├── Backend/
│   ├── Palabras.slnx
│   ├── Palabras.Domain/          # entities, FSRS (pure, no deps), session composer, streak rules
│   ├── Palabras.Infrastructure/  # EF Core SQLite, repos, WebPush, backup, importer
│   ├── Palabras/                 # API host, endpoints, BackgroundServices, CLI verbs
│   └── Palabras.Tests/           # xUnit: FSRS golden, streak/skip, composer, importer, auth policies
├── content/
│   ├── schema/word.schema.json
│   ├── style-guide.md
│   ├── lint/latam-denylist.json
│   └── words/*.json
└── tools/
    ├── fsrs-golden/           # python + py-fsrs 6.3.2 → Backend/Palabras.Tests/Srs/golden.json (was node + ts-fsrs)
    └── content-lint/          # node script, runs in CI
```
- **Branching:** trunk (`main`) with short feature branches and PRs (self-merge). Protect `main` from force-push only.
- **CI (`ci.yml`)**, one job on push/PR:
  1. `dotnet build` + `dotnet test`;
  2. `npm ci` + `eslint` + `tsc -b` + `vite build`;
  3. content lint + schema validation;
  4. `docker build`.
  - On `main` only, a second step pushes to `ghcr.io/<user>/palabras:latest` + `:sha`, and Unraid pulls with `docker compose pull && up -d`. The Unraid side needs a one-time `docker login ghcr.io` with a read-only PAT. That is a user-run mutating action, not an agent action.
  - Nothing else: no deploy automation, no Dependabot beyond the defaults.
- **README skeleton:** What it is · Screens (learner / reviewer) · Stack · Local dev · Content authoring & import · Deploy on Unraid (proxy integration, DNS/TLS, env) · Install as PWA (iOS/Android) & notifications · Backup/restore · Feedback export & style-guide loop · Troubleshooting (push, cookies after restart).
- **Seed/import:** `content/words/*.json` plus CLI verbs on the main binary: `import <dir>`, `export-feedback [--since] [--no-notes]`, `set-password <user>`, `gen-vapid`. Dev convenience: `npm run content:lint`, and `dotnet run --project Backend/Palabras -- import ../../content`.

## 12. Open questions (answer before M0; recommended default in bold)

1. **Exposure:** public internet behind the existing reverse proxy, or VPN/Tailscale-only? Tailscale-only means no custom-domain public cert, which complicates push.
   - Default: **public via proxy + HTTPS + rate limiting + no registration**.
2. **Phones:** which OS and version do you and your fiancée use? iPhone < 16.4 means no push at all.
   - Default: **design for both, with the ICS fallback always available**.
3. **Stack:** .NET backend (Budgeteer shape), or all-TS (Hono + ts-fsrs)?
   - Default: **.NET (Budgeteer shape)**.
4. **Missed day:** should it auto-consume the weekly skip, like a streak freeze? The other option is that only an explicit tap counts as a skip, and any miss breaks the streak.
   - Default: **auto-consume**.
5. **Content generation:** OK to draft the seed list and alternatives with an LLM offline, with her review as the quality gate? Also confirm the subdomain name.
   - Default: **yes, `palabras.<domain>`**.

## Evidence check

Re-read of each cited line:
- `Budgeteer/AGENTS.md:12`, `:14`, `:66`, `:75`, `:105`: grep output matched the claims.
- `Budgeteer/Dockerfile:2`, `:13`, `:23`, `:26`, `:32-33`: read, match.
- `Budgeteer/docker-compose.yml:2`, `:7`, `:10`, `:15`, `:18-19`: read, match.
- `nas-docker-compose/radarr/docker-compose.yml:9` (`PUID=1026`): grep match.
- `nas-docker-compose/tailscale/docker-compose.yml`: read, 0 lines.

Result: **verified: re-read OK.**

Unverified items are tagged `UNVERIFIED:` inline (FSRS default weights from memory, push library maintenance, iOS notification actions, .NET 10 passkeys, PCIC URL, Navarra example words, user timezone, local toolchain because the shell was denied, the live Unraid proxy).
