# T-20261017-1516 / Step 02 — Design amendment (delta to `01-spanish-pwa-design.md`)

This document is a **delta**. Anything not superseded here stays as written in `01-spanish-pwa-design.md` (cited below as `01:<line>`). Where a delta says **REPLACES**, delete the cited lines and use the text here. **AMENDS** means keep the cited lines and apply the listed edits.

Design only. No repo, no code.

## Δ0. Decisions taken from the user's answers

| # | User answer (short) | Decision | Supersedes |
|---|---|---|---|
| Q1 | Behind their own nginx, own domain, PWA | Public HTTPS behind the **existing nginx** on Unraid. The app binds a loopback host port, and compose ships no proxy | `01:315-392`, `01:473-474` |
| Q2 | OnePlus 15 + OnePlus Tab 3 "as ultrawide" | **Android Chrome only.** Phone portrait plus wide tablet landscape | `01:21`, `01:230-233`, `01:398`, `01:420-421`, `01:475-476` |
| Q3 | Minimal, .NET 10 (not 11 RC) | Budgeteer shape on **.NET 10** confirmed, unchanged | `01:477-478` (closed) |
| Q4 | One daily reminder ~22:00, configurable, mute in the notification | Single reminder, default 22:00, with a notification mute action | `01:58`, `01:228-249` |
| Q5 | No LLM at runtime; LLM council for authoring; fiancée has final say | Offline **council CLI**, an approved-only scheduling gate, and pre-generated alternatives | `01:251-313` (parts), `01:262` |
| Supervisor | Explicit skip only, 1 per rolling 7 days | Rolling-window explicit skip. No `frozen` status | `01:173-180`, `01:479-480` |

---

## ΔA. Deployment behind the existing nginx — REPLACES `01:315-366` and the `PROXY_NETWORK`/`FORWARDED_KNOWN_NETWORK` items in `01:368`; AMENDS `01:383-392`

### A.1 Topology
```
Internet ──443──> nginx (existing, on Unraid, TLS with the user's certs)
                    └─ proxy_pass http://127.0.0.1:18480 ──> container palabras:8080 (plain HTTP)
```
- One origin, `https://palabras.<domain>`, serves the SPA, `/api/*`, `/sw.js`, `/manifest.webmanifest` and `/healthz`. No CORS, and the PWA scope is `/`.
- Compose has **no proxy service, no `networks: proxy external`, and no `PROXY_NETWORK`**.

### A.2 docker-compose.yml (replaces the block at `01:334-361`)
```yaml
services:
  palabras:
    image: ghcr.io/<gh-user>/palabras:${PALABRAS_TAG:-latest}
    container_name: palabras
    restart: unless-stopped
    user: "99:100"
    env_file: .env
    environment:
      - ASPNETCORE_URLS=http://+:8080
      - ConnectionStrings__Default=Data Source=/data/palabras.db
      - TZ=${TZ:-Europe/Madrid}
    volumes:
      - ${APPDATA:-/mnt/user/appdata/palabras}:/data
    ports:
      - "127.0.0.1:${APP_PORT:-18480}:8080"     # loopback only; nginx is the sole public entry
    networks: [palabras_net]
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 20s
networks:
  palabras_net:
    ipam:
      config:
        - subnet: 172.31.80.0/24     # fixed so KnownIPNetworks can be exact; M0 checks it is free (`docker network inspect`)
```
**M0 precondition (read-only, not a blocker):** `ssh unraid-docker 'docker ps --format "{{.Names}}\t{{.Image}}\t{{.Ports}}"'`, then `docker inspect` the nginx container's `HostConfig.NetworkMode`.
- If nginx runs **on the host** or in a container with `network_mode: host`, then `127.0.0.1:18480` is reachable. Use the setup above. This is the default.
- If nginx is a container on a **bridge** network, its `127.0.0.1` is its own loopback, so the loopback bind won't work. In that case, drop `ports:`, join nginx's network as `external`, use `proxy_pass http://palabras:8080`, and set `KnownIPNetworks` to that network's subnet. Everything else in this section stays the same.
- `18480` must not be in use: check the `Ports` column from the command above.

### A.3 nginx server block (document in README "Deploy on Unraid")
```nginx
server {
    listen 80;
    server_name palabras.<domain>;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name palabras.<domain>;

    ssl_certificate     /path/to/existing/fullchain.pem;   # the user's existing certs (wildcard or per-host)
    ssl_certificate_key /path/to/existing/privkey.pem;

    # HSTS at the edge only; no includeSubDomains/preload unless every subdomain is HTTPS.
    add_header Strict-Transport-Security "max-age=31536000" always;

    client_max_body_size 2m;          # JSON batches only; content import is a CLI/startup job, not an upload

    location / {
        proxy_pass http://127.0.0.1:18480;
        proxy_http_version 1.1;
        proxy_set_header Connection "";                 # keep-alive to upstream; no websocket/Upgrade needed
        proxy_set_header Host              $host;       # app sees the real host; no X-Forwarded-Host needed
        proxy_set_header X-Forwarded-For   $remote_addr; # OVERWRITE (nginx is the edge) — not $proxy_add_x_forwarded_for, so clients can't spoof
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 30s;
    }
}
```
- **No websocket/`Upgrade` headers.** The app has no SignalR or WS. Push goes through FCM, not through this origin.
- **nginx gotcha:** an `add_header` inside a `location` drops every server-level `add_header`. Keep HSTS at server level, and set `Cache-Control` for `/sw.js` and `/manifest.webmanifest` **in the app** (`no-cache`), not in nginx.
- Cert renewal stays whatever the user already runs. The app is not involved.

### A.4 App settings (AMENDS `01:220`, `01:223`)
- Forwarded headers, registered **first** in the pipeline:
  ```csharp
  builder.Services.Configure<ForwardedHeadersOptions>(o =>
  {
      o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
      o.ForwardLimit = 1;
      o.KnownIPNetworks.Clear();
      o.KnownProxies.Clear();
      o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(builder.Configuration["Forwarding:KnownNetwork"]!)); // "172.31.80.0/24"
  });
  // app.UseForwardedHeaders();  ← first middleware
  ```
  - Use `KnownIPNetworks`, not `KnownNetworks`. The old property is obsolete in .NET 10 (warning `ASPDEPR005`, source: learn.microsoft.com "IPNetwork and ForwardedHeadersOptions.KnownNetworks are obsolete").
  - Requests arriving through the loopback port reach the container from the bridge gateway of `palabras_net` (`172.31.80.1`). Trusting that `/24` is exact.
  - UNVERIFIED: the source IP is the gateway with Docker's default userland-proxy/iptables. M1 logs `HttpContext.Connection.RemoteIpAddress` once to confirm it.
- Do **not** use `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. It trusts any peer. That is acceptable here only because of the loopback bind, but the explicit config is clearer and survives the bridge-network fallback.
- **Secure cookie — exact setting:** `options.Cookie.SecurePolicy = CookieSecurePolicy.Always;` on the cookie auth options.
  - The cookie name `__Host-palabras` (`01:209`) *requires* `Secure`, `Path=/` and no `Domain`. The browser silently refuses to store it otherwise.
  - The container sees plain HTTP. If forwarded headers are misconfigured, `SameAsRequest` would drop `Secure`, and login would "work" but never stick. `Always` makes the cookie independent of that.
  - Forwarded-proto is still needed so that `Request.IsHttps` is true, generated absolute URLs (`APP_BASE_URL` fallback) are `https`, and rate limiting keys on the real client IP.
- No `UseHsts()` and no `UseHttpsRedirection()` in the app, because nginx owns both. This avoids redirect loops if the forwarded proto is ever missing.
- Env surface delta (`01:368`):
  - **remove** `PROXY_NETWORK`, `FORWARDED_KNOWN_NETWORK`, `FALLBACK_NOTIFY_URL`;
  - **add** `APP_PORT=18480`, `Forwarding__KnownNetwork=172.31.80.0/24`;
  - `TZ` default becomes `Europe/Madrid` (container clock only; user-facing time uses the per-user timezone, ΔC.4).

### A.5 DNS/TLS steps — REPLACES `01:383-392`
1. Add DNS `palabras.<domain>`: an `A` record to the WAN IP, or a `CNAME` to the existing DDNS name. Use whatever already routes 443 to this nginx.
2. Drop the A.3 server block into the nginx config and run `nginx -t`, then reload. This is a user-run mutating action on Unraid, not an agent action.
3. Verify:
   - `curl -I https://palabras.<domain>/healthz` → 200 with a valid cert and the `strict-transport-security` header;
   - `curl -I http://palabras.<domain>/` → 301;
   - the login response carries `Set-Cookie: __Host-palabras=…; secure; httponly; samesite=lax; path=/`;
   - Chrome DevTools → Application → Manifest shows no installability errors.

---

## ΔB. Client targets: Android Chrome only — AMENDS §1 frontend row (`01:34`), §5, §9 PWA (`01:394-398`); REPLACES `01:230-233`

### B.1 Platform facts we design on
- **Web Push on Android Chrome** is delivered via FCM to the service worker even when the PWA is closed. The iOS caveats (`01:231`) are removed entirely.
- **Notification actions:** Chrome shows **at most 2 actions** per notification. Source: Chrome for Developers, "Notification Actions in Chrome 48": "Currently Chrome only supports two actions on each notification… check `Notification.maxActions`".
  - Design for 2. The SW reads `Notification.maxActions` and trims the list to that count.
  - The notification body tap is always the third, implicit "action".
- **OEM battery management:** OxygenOS can delay background delivery. UNVERIFIED for the OnePlus 15 specifically.
  - Mitigation: the in-app setup checklist tells the user to set Chrome's battery usage to "Unrestricted".
  - M8 tests delivery on the real phone with the screen off.
- **ICS feed:** kept only as an **optional, later** extra (L6). It is no longer "always on", and it is not the primary fallback.

### B.2 Install prompt (Android Chrome) — AMENDS `01:394-398`
Chrome's install criteria (web.dev "What does it take to be installable?"):
- HTTPS;
- a manifest with `name`/`short_name`, 192 px **and** 512 px icons, `start_url`, `display` ∈ {standalone, fullscreen, minimal-ui, window-controls-overlay};
- `prefer_related_applications` absent or false;
- engagement: at least one tap and at least 30 s on the page;
- not already installed.

Manifest additions:
- `"orientation": "any"`, so the tablet can run landscape;
- `"id": "/"`;
- maskable icons;
- `screenshots` for both a narrow (`form_factor: "narrow"`) and a wide (`form_factor: "wide"`) form, which gives Chrome's richer install UI.

In-app behaviour:
- Capture `beforeinstallprompt`. Show an "Instalar app" button in settings and on the home banner after the first completed session or review sitting. Call `prompt()` only from that button's click.
- Detect an installed app with `matchMedia('(display-mode: standalone)')` and hide the button.
- Remove the iOS "Share → Add to Home Screen" instructions (`01:398`).

### B.3 Push permission — AMENDS `01:244` area
- `Notification.requestPermission()` and `pushManager.subscribe()` are called **only inside a click handler**, on the "Activar recordatorio" button (onboarding step 2 or Settings). Never call them on page load: Chrome's quieter permission UI and auto-blocking penalise unprompted requests.
- If permission is `denied`, show the steps to re-enable it (Chrome site settings) and keep the in-app banner and badge.
- Only the **learner** gets the subscription UI. The reviewer has no reminder in MVP.

### B.4 Responsive layout (new subsection in §4; applies to every screen from M4 on)
Breakpoints are the Tailwind v4 defaults. Components use **container queries** (`@container`, built into Tailwind v4), so a card adapts to its pane, not to the viewport.

| Range (CSS px width) | Target | Layout |
|---|---|---|
| `< 768` | OnePlus 15 portrait | Single column, bottom tab bar (Hoy / Palabras / Ajustes; reviewer: Revisar / Aprobadas / Ajustes). Session full-screen. Reviewer: one card at a time, alternatives in a bottom sheet |
| `768–1279` (`md`/`lg`) | Tablet portrait, phone landscape | Two panes where useful. Learner: session (left, ~60%) plus "today" side panel (progress, streak, words introduced today). Reviewer: queue grid with 2 columns |
| `≥ 1280` (`xl`, `2xl`) | **OnePlus Tab 3 landscape ("ultrawide")** | Learner: session pane plus a word-list pane (search/filter of approved words, stage chips). Reviewer: a 3-column grid at `2xl` (2 at `xl`), with the detail/alternatives sheet as a **right-side drawer** instead of a bottom sheet |

- **Ultrawide rule:** the app shell has `max-w-[1600px] mx-auto`.
  - The session card has `max-w-2xl` (≈ 42 rem) and is centred in its pane.
  - Prose (examples, notes) uses `max-w-prose` (≈ 65 ch).
  - Never stretch line length, and never scale fonts with the viewport.
  - Extra width becomes columns or whitespace, not wider cards.
- Touch targets ≥ 48 px. The typed exercise listens to `visualViewport` resize, so the soft keyboard doesn't cover the input in landscape.
- Device CSS sizes are **UNVERIFIED**. M0 ships a dev-only `/debug/viewport` page that prints `innerWidth×innerHeight` and `devicePixelRatio`. Run it on both devices in both orientations, and record the results in `AGENTS.md`. Playwright smoke viewports use the measured values.

---

## ΔC. Reminders — REPLACES §6 (`01:228-249`) entirely

### C.1 Settings (learner)
- `reminder_enabled` (default on);
- `reminder_time` (default **22:00**, local);
- `reminder_muted_until` (local date or null);
- `reminder_send_window_min` (default 90).

The `last_call_time` setting is removed.

### C.2 Sender — `ReminderService : BackgroundService`, `PeriodicTimer(1 min)`
For the learner, each tick:
1. Compute `now_local` and `local_date` in `users.timezone` (C.4).
2. Send only if **all** of these hold:
   - `reminder_enabled`;
   - `reminder_muted_until` is null or `< local_date`;
   - `reminder_time ≤ now_local < reminder_time + reminder_send_window_min`;
   - no `day_status` row for today (not done, not skipped);
   - no in-progress session;
   - no `reminder_log(user, local_date, 'daily', channel)` row exists.
3. Payload (JSON, ≤ 4 KB):
   ```json
   {"v":1,"kind":"daily","local_date":"2026-10-20","expires_at":"2026-10-20T21:59:00Z",
    "est_min":9,"skip_available":true,"skip_next_available":null,"url":"/?start=1"}
   ```
4. Web Push headers:
   - `TTL` = seconds until `min(local 23:59, send time + window)`, minimum 60;
   - `Urgency: normal`;
   - `Topic: daily-<local_date>`, so a re-send replaces rather than stacks.
5. Handle 404/410 → delete the subscription. Other errors → `failure_count++` (unchanged from `01:241`).
6. `reminder_log` UNIQUE still prevents a double send after a restart (`01:242`, `01:127`).

### C.3 Notification UI and actions
The SW `push` handler calls `showNotification` with `tag: 'daily'` and `renotify: false`.

| Element | When skip available | When skip used this window |
|---|---|---|
| Title | "Tu repaso de hoy (~9 min)" | "Tu repaso de hoy (~9 min)" |
| Body | "Racha 12 · salto disponible" | "Racha 12 · salto usado — vuelve el jue 23" |
| Action 1 | `skip` "Saltar hoy" | *(absent)* |
| Action 2 | `mute` "Silenciar" | `mute` "Silenciar" |
| Body tap | open `/?start=1` (starts session) | same |

**Recommendation vs the brief's `[Hecho] [Saltar hoy] [Silenciar]`:** Chrome renders only 2 actions (B.1), and "Hecho" cannot be honest from a notification. Done means a completed session (`01:171`), so a button can't make it true. "Hecho" therefore becomes the **body tap**, which opens and starts the session. The two real buttons are `Saltar hoy` and `Silenciar`.

What is routable where:

| Action | Handled in SW without opening the app? | Mechanism |
|---|---|---|
| `skip` | **Yes** | `notificationclick` with `event.action==='skip'` → `event.waitUntil(fetch('/api/learn/day/skip', {method:'POST', credentials:'same-origin', headers:{'X-Palabras':'1','Content-Type':'application/json'}, body:JSON.stringify({local_date, source:'notification'})}))`. The SW then shows a confirmation notification: 200 → "Saltado ✓ — hasta mañana". 409 → "Sin salto disponible — ¿5 min?" with body tap = start. 401 (cookie expired) → open `/login?next=/ajustes`. The same-origin SW fetch sends the `__Host-` cookie, and the SW can set the custom header, so CSRF rule `01:222` holds |
| `mute` | **No — opens the app** | `clients.openWindow('/ajustes/recordatorio?from=notif&d=<local_date>')`, or focuses an existing client and navigates it. The mute choice happens in the app (C.3.1). A deliberate design choice: no silent permanent disable from a stray tap |
| body tap | No — opens the app | `openWindow(payload.url)`; the app sees `?start=1` and starts the session plan |

Every click writes a `notification_actions` row: the SW calls `POST /api/learn/notification-action` (fire-and-forget; skip records itself server-side).

#### C.3.1 "Silenciar" sheet (the deep-link target)
With only one reminder per day, already delivered when the user sees it, "mute today only" has nothing left to mute. Options:
- **"Mañana no"** → `reminder_muted_until = local_date + 1`.
- **"Pausar 3 días" / "Pausar 7 días"** → `muted_until = local_date + N`. Covers a trip or vacation; auto-resumes.
- **"Desactivar recordatorios"** → `reminder_enabled = false`. Re-enable from Settings only.
- **"Cambiar hora"** → time picker, prefilled with the current `reminder_time`.
- Cancel / back.

Muting **never** touches the skip, the streak or FSRS. A muted day without a session is still a missed day. The sheet states this in one line: "Silenciar no salta el día."

### C.4 Timezone
- Source of truth: `users.timezone` (IANA), stored server-side, because the server sends and also decides day boundaries for skip, streak and rollover.
- Default: captured from the device (`Intl.DateTimeFormat().resolvedOptions().timeZone`) at first login, with `Europe/Madrid` as fallback. `Europe/Madrid` and `Europe/Amsterdam` share offsets and DST rules, so A2 (`01:20`) no longer matters.
- **Travel:** on app open, if the device zone ≠ the stored zone, show a banner "¿Usar hora de <zona>?". Changing it takes effect from the **next** local date, so today's skip/streak window doesn't shift mid-day.
- DST: compute 22:00 with `TimeZoneInfo` every tick.
  - Local times that don't exist (spring-forward gap) → the next valid minute.
  - Ambiguous local times → the first occurrence.
  - This only matters if the user picks a time near 02:00–03:00. 22:00 is never affected.

### C.5 Phone off at 22:00 / server down at 22:00
- **Phone off or offline:** FCM holds the message until `TTL` expires, then drops it. The `TTL` (C.2) ends at local 23:59 or earlier, so a phone switched on at 02:00 gets **nothing**, and there is no 02:00 blast.
- **Belt and braces in the SW:** if `Date.now() > payload.expires_at` (clock skew, late delivery), show a **silent** notification (`silent:true`, no vibrate) titled "Repaso pendiente" with no actions, rather than a loud one.
  - Chrome requires a notification to be shown for each `userVisibleOnly` push, so "show nothing" is not an option.
  - UNVERIFIED: whether an immediately closed notification satisfies Chrome's requirement. Don't rely on it.
- **Server down at 22:00:** on startup, the service sends only if `now_local` is still inside `[reminder_time, reminder_time + window)`. Otherwise today's reminder is dropped and logged as `reminder_log.result='window_missed'`, and no catch-up runs at 02:00.
- **Fallbacks:** the in-app banner and `navigator.setAppBadge` (`01:249`) remain. The ICS feed and the webhook move to later (L6).

---

## ΔD. Skip semantics — REPLACES `01:173-180`; REPLACES M5 "done when" (`01:409`)

- **Skip = an explicit action only:** the in-app "Saltar hoy" button or the notification `skip` action. Both call `POST /api/learn/day/skip {local_date, source}`.
  - Allowed only for **today** (the server's local date in `users.timezone`), before local midnight.
- **Allowance: 1 per rolling 7-day window.** Allowed iff there is no non-refunded `day_skips` row with `local_date` in `[today − 6, today]`.
  - `skip_next_available = last_skip_date + 7`.
  - The check and the insert run in one transaction. SQLite's single writer serialises them.
- Responses:
  - 200 = skipped;
  - 200 idempotent if today is already skipped;
  - **409** with `{reason:'skip_unavailable', next_available}`;
  - **409** with `{reason:'already_done'}` if today is already done.
- **Skipping preserves the streak** for that day but does not increment it. A **plain missed day breaks the streak**, and the rollover job never consumes the skip automatically.
  - The `frozen` status is removed.
  - The ISO-week rule and the "≥ 1 done per ISO week" clause (`01:179`) are removed. With 1 skip per 7 days, a streak cannot consist of skips alone.
- **Refund:** if the learner skips and then completes a session the same day, the day becomes `done` and the skip is refunded (`day_skips.refunded_at` is set), so the token is available again.
- **Notification coupling:** `skip_available` is computed at send time (C.2).
  - If the skip is unavailable, the `skip` action is absent and the body says when it returns (C.3).
  - If availability changed between send and click, the API's 409 drives the SW confirmation text.
- **Rollover job** (00:05 local): write `missed` for yesterday if it has no `done` or `skipped` row. It only writes history and never touches cards.
- **FSRS consistency, confirmed:**
  - A skipped day writes **no** `review_log` rows and doesn't modify any `cards` row. Due dates are unchanged; cards simply become overdue.
  - On the next review, FSRS uses the real `elapsed_days`, so lower retrievability is reflected in the S/D update (the forgetting curve `01:146`). There is **no extra penalty term**: a skipped day and a missed day are identical to the scheduler and differ only in the streak.
  - The next day's load is capped by the budget rule (`01:168`).
- M5 tests (replace `01:409`):
  - 2nd skip within 7 days → 409;
  - skip on day N, then day N+7 → allowed;
  - missed day → streak 0, no skip consumed;
  - skip + same-day session → done + refund;
  - DST-week window;
  - timezone change applies from the next day.

---

## ΔE. Content pipeline: LLM council offline, fiancée as gate — REPLACES `01:262`, `01:275-282`, `01:302-306`; AMENDS `01:253-261`, `01:264-273`, `01:297-300`

### E.0 Hard invariant (runtime has zero LLM dependency)
- The production image contains **no** council code, no provider SDKs and no model keys. The Dockerfile copies only `Frontend/`, `Backend/` and `content/` (data); `tools/` is never copied.
- `.env.example` has **no** LLM variables. Nothing at runtime branches on a key being present.
- CI test: `docker run … env | grep -Ei 'openai|anthropic|gemini|openrouter|api_key'` → must be empty. A second test greps the published `wwwroot` and DLLs for provider hostnames → 0 hits.

### E.1 Word lifecycle state machine — REPLACES the `status` semantics at `01:75`, `01:256-260`, `01:298-300`

```
            import(council|manual)
   draft ───────────────────────────► pending_review ◄──────────────┐
     ▲   owner "Enviar a revisión"        │  │  │                     │ owner/council rework
     │                                    │  │  └─ "Cambiar"(flag) ─► needs_change
     │                                    │  │                         (not scheduled)
     │              "Vale" / alt-pick /   │  └─ "No" (no alt) ──────► rejected (terminal unless owner re-submits → draft)
     │              edit-in-place         ▼
     │                                 approved ──── "Revisar otra vez"/owner demote ──► pending_review | needs_change | rejected
     │                                    │
     └──── owner edits a draft            └── owner "Retirar" ──► retired (any state → retired; terminal)
```

| From | Event | To | Actor |
|---|---|---|---|
| — | council bundle import | `pending_review` (or `draft` with `--hold`) | startup sync / CLI |
| — | manual entry in admin | `draft` | owner |
| `draft` | release | `pending_review` | owner |
| `pending_review` | Vale (approve) | `approved` | reviewer |
| `pending_review` | pick alternative | `approved`, `version++` | reviewer |
| `pending_review` | edit in place, save | `approved`, `version++` | reviewer |
| `pending_review` | Cambiar (flag fields + optional note, no fix supplied) | `needs_change` | reviewer |
| `pending_review` | No (reject, no alternative) | `rejected` | reviewer |
| `needs_change` | owner edit or council `rework` re-import | `pending_review`, `version++` | owner |
| `rejected` | re-submit (rare) | `draft`, `version++` | owner |
| `approved` | demote ("Revisar otra vez" in her Aprobadas list, or owner) | `pending_review` / `needs_change` / `rejected` | reviewer / owner |
| any | retire | `retired` | owner |

- **The hard rule:** only `status='approved'` words are scheduled by FSRS and appear anywhere in the learner UI (session, word list, stats).
  - Enforced twice: (1) cards are **created only on approval**, not at import; (2) every learner query joins `words.status='approved'` and `cards.suspended_reason IS NULL`.
  - **Removed:** the non-stalling rule (`01:262`, unverified words in the learner session with a "sin verificar" badge). It contradicts the gate.
- **Demotion keeps everything:**
  - On `approved → *`, the word's cards get `suspended_reason='word_not_approved'`. FSRS state (S, D, due, reps, lapses) is untouched, and `review_log` is append-only, so nothing is lost.
  - On re-approval:
    - if the primary `lemma` and the sense's `en_gloss` are unchanged versus the last approved version → clear `suspended_reason`. The card is simply overdue.
    - if the meaning changed (lemma or primary gloss) → `cards.epoch++` and reset to New. Old `review_log` rows keep their `card_epoch`, so the optimiser (L4) can exclude pre-reset history.
    - Example/note-only changes never reset (`01:258` rule kept).
- Every version is snapshotted in `word_revisions`, so "original preserved" is a row, not a column.
- **Stall mitigation without the badge:**
  - the session runs with reviews only when no approved new words are left (still a valid "done" day; the refuerzo floor `01:169` applies);
  - the owner sees "Cola aprobada: N palabras nuevas restantes (≈ N/8 días)" and gets an in-app warning below 14 days;
  - batch-approve (E.4) keeps her throughput high.

### E.2 Council CLI — `tools/council/`, **TypeScript Node CLI**
Choice: TS over a `dotnet` verb.
- It sits next to the existing node tools (`tools/fsrs-golden`, `tools/content-lint`, `01:456-458`).
- It reuses the same `content/schema/word.schema.json` (via ajv) and the lint module directly.
- It uses the Vercel AI SDK (`ai` + `@ai-sdk/*` providers), which gives one structured-output interface across providers.
- Physically separate from `Backend/`, so the runtime invariant (E.0) is structural, not a convention.
- Rejected alternative: a `Palabras.dll council` verb using `Microsoft.Extensions.AI`. It would put provider packages into the production binary.

Commands:
```
npx council propose --topic food --cefr A2 --count 40 [--models <list>] [--hold] [--dry-run]
npx council rework  --from export.jsonl         # needs_change items → regenerated candidates
npx council report  --since 2026-10-01          # per-model / per-prompt acceptance stats (L2)
```

Pipeline for `propose`:
1. **Context build (privacy-checked, see ΔH):**
   - topic, CEFR, count;
   - `content/style-guide.md`;
   - the schema;
   - an exclusion list = all existing `lemma|pos` pairs (any status, including `rejected`) from `content/exports/lexicon.json`. This is produced by `dotnet Palabras.dll export-lexicon`, or pulled by the owner from `/api/admin/lexicon.json`.
2. **Phase 1 — Propose (fan-out):** the same prompt `prompts/propose.v<N>.md` goes to every model in parallel. Each returns `{candidates:[WordCandidate]}`, schema-validated per model.
   - Invalid items are dropped per model and counted in `council_model_results.invalid_count`.
3. **Normalise:**
   - NFC; lowercase; strip the article from `lemma` into `article`;
   - POS mapped to the enum;
   - `key = slug(lemma)-pos[0]`;
   - EN gloss normalised (lowercase, strip a leading "to "/"a "/"the ").
4. **Cluster and dedupe** by `(lemma_norm, pos)` across models. `proposal_support = |models proposing| / |models succeeded|`.
5. **Merge fields per cluster:**
   - categorical fields (`article`, `gender`, `cefr`, `register`, `region`) take the plurality value; ties go to the first model in the configured order, and the tie is recorded;
   - free-text fields (`en_gloss`, `example_es/en`, `regional_note`) take the plurality value as primary. **Every other distinct value becomes an alternative**, with the models that produced it. Nothing is discarded.
6. **Phase 2 — Critique (fan-out):** each model receives all merged candidates (batched at 25) and returns per candidate:
   - `verdict ∈ {ok, issue}`;
   - `issues[]` from the codes `latam_lexicon | unnatural_es | wrong_gloss | wrong_gender | cefr_mismatch | register | duplicate_meaning`;
   - plus `suggested_alternatives` (≥ 2 EN glosses, ≥ 1 example pair, and a regional/register variant if relevant).
7. **Score:** `agreement = critique_ok / critique_succeeded` (primary), and `proposal_support` (secondary, shown as a tie-breaker and sort key).
   - `alta` ≥ 0.75;
   - `media` 0.50–0.74;
   - `< 0.50` → written to `dropped.jsonl` with reasons, **never** queued.
   - Field-level disagreement never drops a word; it becomes alternatives.
8. **Top up alternatives:** merge the alternatives from phase 1 with the critique `suggested_alternatives`, dedupe, and rank by `support` (number of models). Enforce the contract (E.3).
   - If a candidate is still below the minimum, make one top-up call to the first healthy model.
   - If it is still short, drop it to `dropped.jsonl` (`reason: insufficient_alternatives`).
9. **Lint:** run `tools/content-lint` (LatAm denylist, vosotros, `ustedes`, `01:308-312`) on the primary values **and** every alternative.
   - A LatAm hit in a primary value with a clean alternative available → swap them, and record the swap in `lint_actions`.
   - A LatAm hit with no clean alternative → the alternative is deleted. A primary hit with no fix → `lint_warnings` shown to her with a red chip.
10. **Write the bundle** (E.2.1), then `council validate <dir>` (schema + lint + manifest hash). CI runs the same validate on every bundle in `content/council/`.

**Rework mode:** the input is `needs_change` items from the feedback export. It carries word content, the flagged fields and her structured edit diffs, but **not** free-text notes (ΔH). The same pipeline runs with a `rework.v<N>.md` prompt that asks for corrected fields only. Output bundles have `mode:"rework"`; they bump the version and return the word to `pending_review`.

#### E.2.1 Bundle format — `content/council/<run_id>/`
```
manifest.json
candidates.jsonl        # one WordCandidate per line → imported
dropped.jsonl           # audit only, never imported
raw/<model-id>.<phase>.json   # gitignored; provider raw responses for local debugging
```
`manifest.json`:
```json
{"schema":"palabras.council.manifest.v1","run_id":"01JAB3…(ULID)","mode":"propose",
 "prompt_version":{"propose":"v3","critique":"v2"},"style_guide_sha256":"…",
 "request":{"topic":"food","cefr":"A2","count":40},
 "models":[{"id":"anthropic:claude-sonnet-5-5","phases":{"propose":"ok","critique":"ok"},"latency_ms":{"propose":51234,"critique":40211},"tokens":{"in":5200,"out":21400},"cost_usd_est":0.31},
           {"id":"<provider>:<model>","phases":{"propose":"failed","critique":"ok"},"error":"timeout after 300s"}],
 "models_requested":4,"models_succeeded":{"propose":3,"critique":4},"reduced_confidence":true,
 "counts":{"candidates":34,"dropped":9},"candidates_sha256":"…","created_at":"2026-10-21T18:00:00Z"}
```
`candidates.jsonl` line (extends the `01:287-295` entry shape; new fields marked):
```json
{"key":"patata-n","lemma":"patata","article":"la","gender":"f","pos":"noun","cefr":"A1","topic":"food",
 "region":"ES","register":"neutral","latam_equivalent":"papa","forms":{"plural":"patatas"},
 "senses":[{"en_gloss":"potato","en_accepted":["spud"],"es_accepted":[],
            "example_es":"¿Nos pones unas patatas bravas?","example_en":"Can you bring us some patatas bravas?"}],
 "alternatives":[
   {"field":"gloss","sense":0,"en":"potatoes (as a dish)","support":2,"models":["m1","m3"]},
   {"field":"gloss","sense":0,"en":"chips / fries (in context)","support":1,"models":["m2"],"rationale":"patatas fritas"},
   {"field":"example","sense":0,"es":"Compro patatas en el mercado.","en":"I buy potatoes at the market.","support":2,"models":["m2","m4"]},
   {"field":"regional_variant","es":"—","rationale":"no Navarra-specific form","support":0,"models":[]}],
 "council":{"run_id":"01JAB3…","agreement":0.75,"band":"alta","proposal_support":1.0,
            "critique":[{"model":"m1","verdict":"ok"},{"model":"m2","verdict":"issue","issues":["register"]}],
            "lint_warnings":[],"lint_actions":[]}}
```

#### E.2.2 Import into the app (no runtime LLM)
- The `content/` folder, including `council/*`, is copied into the image at `/app/content`, as data only.
- `ContentSyncService` runs at startup. It imports every bundle whose `run_id` is not yet in `council_runs`, after verifying `candidates_sha256`. The import is idempotent.
- Manual verb: `dotnet Palabras.dll import-council <dir>`. The original `import <dir>` (`01:297`) stays for hand-written `content/words/*.json` and lands those words in `draft`.
- Import order: `council_runs` row, `council_model_results` rows, then per candidate the `words` (status per E.1), `senses`, `word_alternatives`, `word_provenance` and the `word_revisions` v1 snapshot.
  - An existing `key` with status `approved` is **never** overwritten by a `propose` bundle; the duplicate is logged and skipped.
  - Only `rework` bundles may version an existing word.

#### E.2.3 Provider config and failure handling
- `tools/council/.env.council` (gitignored; never on the server or in CI):
  ```
  COUNCIL_MODELS=anthropic:claude-sonnet-5-5,openai:<model>,google:<model>,mistral:<model>
  ANTHROPIC_API_KEY=… OPENAI_API_KEY=… GOOGLE_GENERATIVE_AI_API_KEY=… MISTRAL_API_KEY=…
  # or a single OPENROUTER_API_KEY with openrouter:<vendor>/<model> ids
  COUNCIL_MIN_MODELS=2
  COUNCIL_TIMEOUT_S=300
  COUNCIL_TEMPERATURE=0.4
  ```
  - `--models` on the CLI overrides `COUNCIL_MODELS`.
  - Model IDs other than Claude are placeholders, chosen at M3 from current provider catalogues. Use 3–5 models from **different vendors**, which gives real diversity.
- Expectations, per model per 40-word propose batch (UNVERIFIED estimates; the manifest records actuals and `council report` shows them):
  - about 5k input and 20k output tokens;
  - 30–120 s;
  - about $0.05–0.50 depending on tier;
  - critique is about half that;
  - a full run with 4 models ≈ $1–3 and 3–5 min wall-clock (parallel).
- Failures:
  - each call is retried twice with exponential backoff on 429/5xx/timeout;
  - a schema-invalid response is retried once with a "fix to schema" reprompt;
  - **1 of N fails** → continue with N−1, set `reduced_confidence: true`, and compute agreement over the models that succeeded. The reviewer UI shows a small "consejo reducido (3/4)" tag;
  - **fewer than `COUNCIL_MIN_MODELS` succeed in either phase** → the CLI exits non-zero, writes no `candidates.jsonl`, and **nothing enters the queue**. `raw/` and a `manifest.json` with `"status":"failed"` are kept for debugging. Validate refuses failed manifests.
  - **All fail** → same as above.
- Determinism and audit: prompts are versioned files (`prompts/propose.v3.md`). The manifest stores the versions and the style-guide hash. `--dry-run` prints the exact prompts without calling anything, which is useful for the ΔH privacy check.

### E.3 Alternatives contract — AMENDS `01:87-91`, `01:304`
- Per **primary sense**:
  - `gloss`: **2–4** alternatives (minimum 2 enforced by the CLI and by the import validator);
  - `example`: 1–3 ES/EN pairs;
  - `regional_variant` (Navarra/peninsular regional form) and `register_variant` (colloquial/formal counterpart): 0–2 each, when relevant. An empty set is valid; "no variant" is never padded with junk.
  - `lemma` alternatives (a synonym that could replace the headword): 0–2.
- Everything is stored pre-generated in `word_alternatives`. Rejecting with an alternative is instant and offline (the queue is prefetched), and **the app never calls a model**.
- Picking an alternative swaps the values: the old value is inserted as an alternative with `source='previous'`, so she can undo.

### E.4 Reviewer experience (`/revisar`) — REPLACES `01:253-261`
- **Ordering:** grouped **by topic**, with the topic having the most pending words first. Within a topic, sort by `agreement` descending, then `proposal_support` descending, then `frequency_rank`.
  - Rationale: topic context makes judging faster, and high-agreement items first give quick wins and make batch-approve natural.
  - `rework` items (her own earlier flags) jump to the front.
- **Sitting size:** 15 by default (client setting 5–30, localStorage). After 15: "¿Otras 15?" or "Listo por hoy". There are no reminders for her in MVP.
- **Card content:**
  - everything in `01:254`;
  - an agreement chip (`alta`/`media`) and "consejo reducido" when relevant;
  - lint warnings in red;
  - `rework` badge showing her previous flag.
  - Model names are **not** shown to her (noise). They are visible in the owner admin view only.
- **Actions** (big buttons, one tap):
  - **Vale ✓** → `approved`.
  - **No ✗** opens the alternatives sheet (bottom sheet on phone, right drawer at ≥ xl):
    - field tabs Traducción / Ejemplo / Variante / Palabra;
    - chips per field (ranked by `support`; shown with a "2/4" support hint);
    - tapping a chip applies the change and approves (`approve_alt`);
    - "Rechazar palabra" (no alternative) → `rejected`;
    - "Ninguna vale…" → **Cambiar** below.
  - **Editar ✎** → inline edit of any text field (gloss, accepted answers, example ES/EN, note, article/gender pickers).
    - The original is shown struck-through above the field.
    - Save = `approve_edit`: `version++`, the snapshot goes to `word_revisions`, and the diff is stored in `word_reviews.edit_diff`.
    - Server-side, the edit runs through the same lint. A LatAm hit shows a soft warning, and she can still save: she has the final say.
  - **Cambiar ⚑** → flag fields plus an optional note, no fix → `needs_change` (input to `council rework`).
- **Batch approve:** a per-topic button "Aprobar las de alta concordancia (N)".
  - It opens a compact checklist of `alta` items with no lint warnings, all pre-checked. She unchecks and confirms.
  - It writes **one `word_reviews` row per word** with `via_batch=1`.
  - Hard cap of 25 per confirm.
- **Her "Aprobadas" list:** searchable, with **"Revisar otra vez"** (demote → `pending_review`; cards suspended per E.1).
- **Offline:** prefetch the next 30 items plus their alternatives. Verdicts and edits queue in IndexedDB with `client_review_id` (`01:261`, kept).
- **Training signal per action** (all in `word_reviews`): action, field, chosen alternative (with its `support` and models via `word_provenance`), edit diff, `via_batch`, `council_run_id`, `agreement_at_review`, and time-to-decision (ms).
  - Batch approvals are a weaker signal and are flagged as such.

### E.5 Learning loop without runtime LLM — REPLACES `01:264-282`
1. `GET /api/admin/feedback/export.jsonl?since=…` and the `export-feedback` CLI keep the `01:266-273` shape, bumped to `palabras.feedback.v2`. New fields: `action`, `edit_diff`, `via_batch`, `council_run_id`, `agreement_at_review`, `chosen_alternative_support`, `chosen_alternative_models`, `prompt_version`.
   - **Notes are excluded by default.** `?includeNotes=true` is opt-in and owner-only, for local reading; the council never ingests it (ΔH).
2. `council report` (L2) reads the export plus the manifests. It reports:
   - acceptance rate per model, per prompt version, per topic and per CEFR;
   - which model's alternative she picks most;
   - the top edit patterns (e.g. "ustedes → vosotros", "de acuerdo → vale");
   - the most common rejection issue codes.
3. **Prompt revision hygiene:**
   - prompts and `style-guide.md` change **only by a human-reviewed PR**: new file `propose.v4.md`, never edit `v3` in place;
   - the PR description cites the report numbers;
   - the next runs record the new version, so A/B comparison by `prompt_version` is free.
   - v1 has **no** automatic prompt self-modification and no auto-generated style-guide commits.
   - Distilling her edits into style-guide rules is done by the owner, optionally with an LLM, but only on the stripped export (ΔH).
4. Model roster changes (drop a model with a low acceptance rate) are a config change in `.env.council`, also backed by the report.

---

## ΔF. Schema deltas (apply to the **initial** EF migration; no data exists yet) — AMENDS `01:50-136`

```sql
-- users  (01:51-54)
ALTER users: timezone TEXT NULL;                      -- was DEFAULT 'Europe/Amsterdam'; set from device at first login, fallback 'Europe/Madrid'
ADD   users.timezone_pending TEXT NULL, users.timezone_effective_from TEXT NULL;   -- ΔC.4 next-day switch

-- user_settings  (01:55-60)
ALTER user_settings.reminder_time TEXT NOT NULL DEFAULT '22:00';
DROP  user_settings.last_call_time;
ADD   user_settings.reminder_enabled INTEGER NOT NULL DEFAULT 1,
      user_settings.reminder_muted_until TEXT NULL,                -- local yyyy-MM-dd, inclusive
      user_settings.reminder_send_window_min INTEGER NOT NULL DEFAULT 90 CHECK (reminder_send_window_min BETWEEN 15 AND 120);

-- words  (01:66-78)
ALTER words.status CHECK (status IN ('draft','pending_review','approved','needs_change','rejected','retired'));  -- unchanged set, new semantics (E.1)
ALTER words.source CHECK (source IN ('council','manual','reviewer'));                                               -- 'seed-llm' → 'council'
ADD   words.status_changed_at TEXT NOT NULL,
      words.approved_version INTEGER NULL,            -- last version that was approved (for demotion/re-approval diff)
      words.council_run_id INTEGER NULL REFERENCES council_runs(id),
      words.council_agreement REAL NULL,
      words.council_band TEXT NULL CHECK (council_band IN ('alta','media')),
      words.reduced_confidence INTEGER NOT NULL DEFAULT 0,
      words.lint_warnings TEXT NULL;                  -- JSON array
CREATE INDEX ix_words_review_queue ON words(status, topic_id, council_agreement DESC);

CREATE TABLE word_revisions (
  id INTEGER PRIMARY KEY, word_id INTEGER NOT NULL REFERENCES words(id),
  version INTEGER NOT NULL, snapshot TEXT NOT NULL,   -- JSON: word + senses + alternatives at that version
  created_by TEXT NOT NULL CHECK (created_by IN ('import','council_rework','reviewer','owner')),
  created_at TEXT NOT NULL,
  UNIQUE (word_id, version));

-- word_alternatives  (01:87-91)
ALTER word_alternatives.field CHECK (field IN ('lemma','gloss','example','regional_note','regional_variant','register_variant'));
ALTER word_alternatives.source CHECK (source IN ('council','reviewer','manual','previous'));
ADD   word_alternatives.support INTEGER NOT NULL DEFAULT 0,       -- #models that produced it
      word_alternatives.models TEXT NULL,                          -- JSON array of model ids
      word_alternatives.region TEXT NULL CHECK (region IN ('ES','ES-NA')),
      word_alternatives.register TEXT NULL CHECK (register IN ('neutral','colloquial','formal')),
      word_alternatives.word_version INTEGER NOT NULL;
-- app/validator rule (not expressible as CHECK): per primary sense, 2 ≤ count(field='gloss') ≤ 4 at import.

CREATE TABLE council_runs (
  id INTEGER PRIMARY KEY, run_id TEXT NOT NULL UNIQUE,            -- ULID from manifest
  mode TEXT NOT NULL CHECK (mode IN ('propose','rework')),
  prompt_versions TEXT NOT NULL, style_guide_sha256 TEXT NOT NULL,
  topic TEXT NULL, cefr TEXT NULL, requested_count INTEGER NULL,
  models_requested INTEGER NOT NULL, models_succeeded_propose INTEGER NOT NULL, models_succeeded_critique INTEGER NOT NULL,
  reduced_confidence INTEGER NOT NULL, candidates_count INTEGER NOT NULL, dropped_count INTEGER NOT NULL,
  candidates_sha256 TEXT NOT NULL, created_at TEXT NOT NULL, imported_at TEXT NOT NULL);

CREATE TABLE council_model_results (
  id INTEGER PRIMARY KEY, council_run_id INTEGER NOT NULL REFERENCES council_runs(id),
  model_id TEXT NOT NULL, phase TEXT NOT NULL CHECK (phase IN ('propose','critique','topup')),
  status TEXT NOT NULL CHECK (status IN ('ok','failed','timeout','invalid')),
  latency_ms INTEGER NULL, input_tokens INTEGER NULL, output_tokens INTEGER NULL,
  cost_usd_est REAL NULL, invalid_count INTEGER NOT NULL DEFAULT 0, error TEXT NULL,
  UNIQUE (council_run_id, model_id, phase));

CREATE TABLE word_provenance (
  word_id INTEGER NOT NULL REFERENCES words(id), word_version INTEGER NOT NULL,
  council_run_id INTEGER NOT NULL REFERENCES council_runs(id), model_id TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('proposed','critique_ok','critique_issue')),
  issues TEXT NULL,                                               -- JSON array of issue codes
  PRIMARY KEY (word_id, word_version, council_run_id, model_id, role));

-- word_reviews  (01:129-135)
ALTER word_reviews.verdict → RENAME to action,
      CHECK (action IN ('approve','approve_alt','approve_edit','reject','needs_change','demote'));
ADD   word_reviews.client_review_id TEXT NOT NULL UNIQUE,         -- was a prose note at 01:261
      word_reviews.edit_diff TEXT NULL,                           -- JSON [{field, sense, before, after}]
      word_reviews.via_batch INTEGER NOT NULL DEFAULT 0,
      word_reviews.council_run_id INTEGER NULL REFERENCES council_runs(id),
      word_reviews.agreement_at_review REAL NULL,
      word_reviews.decision_ms INTEGER NULL;
ALTER word_reviews.field CHECK (field IN ('lemma','gloss','example','regional_note','regional_variant','register_variant','article','gender','accepted','whole'));

-- cards  (01:93-101)
ADD   cards.suspended_reason TEXT NULL CHECK (suspended_reason IN ('word_not_approved','sense_removed','manual')),
      cards.epoch INTEGER NOT NULL DEFAULT 0;
DROP  cards.suspended;                                            -- suspended ⇔ suspended_reason IS NOT NULL
DROP INDEX cards(user_id, suspended, due);
CREATE INDEX ix_cards_due ON cards(user_id, due) WHERE suspended_reason IS NULL;
-- cards rows are INSERTed only on word approval (E.1).

-- review_log  (01:108-115)
ADD   review_log.card_epoch INTEGER NOT NULL;

-- day_status  (01:117-120)
ALTER day_status.status CHECK (status IN ('done','skipped','missed'));   -- 'frozen' removed
DROP  day_status.iso_week; DROP INDEX day_status(user_id, iso_week);

CREATE TABLE day_skips (                                          -- skip ledger, rolling 7-day window (ΔD)
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),
  local_date TEXT NOT NULL, source TEXT NOT NULL CHECK (source IN ('app','notification')),
  created_at TEXT NOT NULL, refunded_at TEXT NULL,
  UNIQUE (user_id, local_date));
CREATE INDEX ix_day_skips_window ON day_skips(user_id, local_date) WHERE refunded_at IS NULL;

-- reminder_log  (01:125-127)
ALTER reminder_log.kind CHECK (kind IN ('daily'));                -- 'last_call' removed
ALTER reminder_log.channel CHECK (channel IN ('webpush'));        -- 'webhook' deferred to L6
ADD   reminder_log.ttl_s INTEGER NULL, reminder_log.expires_at TEXT NULL,
      reminder_log.skip_available INTEGER NOT NULL;
-- result values: 'sent','failed','gone','window_missed'

CREATE TABLE notification_actions (
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id),
  reminder_log_id INTEGER NULL REFERENCES reminder_log(id), local_date TEXT NOT NULL,
  action TEXT NOT NULL CHECK (action IN ('open','skip','mute')),
  result TEXT NOT NULL CHECK (result IN ('ok','skip_unavailable','already_done','unauthenticated','error')),
  mute_choice TEXT NULL CHECK (mute_choice IN ('tomorrow','3d','7d','disable','time_changed','cancel')),
  created_at TEXT NOT NULL);
```
EF note: SQLite `ALTER` limits don't matter, because these are edits to the first migration. The `CHECK` constraints go into `HasCheckConstraint` in the entity configs.

---

## ΔG. Delivery plan — REPLACES `01:400-426`

| # | Milestone | Done when | MVP |
|---|---|---|---|
| M0 | Repo + skeleton + CI + deploy hello (**as-is**, `01:404`) | Private repo; CI green; `/healthz` 200 through the **user's nginx** (A.3) on the loopback port; manifest installable; nginx network-mode check done (A.2); `/debug/viewport` measured on both devices | ✓ |
| M1 | Auth + roles + edge correctness | `01:405` criteria, **plus**: cookie `__Host-palabras` has `Secure` through nginx; `RemoteIpAddress` = real client IP (rate limit keyed correctly); forged `X-Forwarded-For` from a client is ignored | ✓ |
| M2 | Content schema v2 + lifecycle state machine + importer (`import`, `import-council`, `ContentSyncService`) + lint + revisions | State-transition unit tests (all E.1 rows; illegal transitions 409); import idempotent; `approved` never overwritten by `propose`; planted "carro" flagged; a hand-written 20-word dev bundle imports to `pending_review`; the CI "no LLM in image" test (E.0) passes | ✓ |
| M3 | **Council CLI** (`tools/council`: propose + critique + merge + lint + bundle + validate; `--dry-run`) | Real run with ≥ 3 vendors → bundle of ~150 candidates across 4 topics; kill one provider (bad key) → `reduced_confidence`; set `COUNCIL_MIN_MODELS` above the healthy count → exit ≠ 0, no `candidates.jsonl`; privacy guard blocks a planted name (ΔH); every candidate has ≥ 2 gloss alternatives | ✓ |
| M4 | **Reviewer gate** `/revisar` (phone **and** wide layout for her screens) + alternatives sheet/drawer + edit-in-place + batch approve + Aprobadas/demote + offline verdicts + feedback export v2 | She reviews 20 words on the OnePlus 15 in < 5 min; chip pick applies instantly offline; batch-approve 10; a demoted word's cards are suspended with FSRS state intact; the tablet landscape grid renders 2–3 columns | ✓ |
| M5 | FSRS-6 port + cards-on-approval + review API (was M3) | Golden vectors (`01:149`); attempts idempotent; cards exist **only** for approved words; re-approval with an unchanged meaning keeps S/D; a changed meaning bumps the epoch | ✓ |
| M6 | Session composer + exercises 1–4 + offline queue, phone layout (was M4) | `01:408` criteria; learner queries return 0 non-approved words (test with a planted `pending_review` word) | ✓ |
| M7 | Day status, streak, **rolling-7-day explicit skip**, refund, rollover (was M5) | ΔD test list | ✓ |
| M8 | Web Push daily 22:00 + `Saltar hoy`/`Silenciar` actions + mute sheet + TTL/window + tz handling (was M7) | Received on the real OnePlus 15 with the screen off (Chrome battery set to Unrestricted); skip from the notification works without opening the app; the skip-used variant shows 1 action; phone in airplane mode 21:50 → 00:30, then back online → **no** notification; restart at 22:05 → sends once; restart at 23:45 → `window_missed` | ✓ |
| M9 | **Wide-tablet learner layout** (session + side panel + word-list pane, ultrawide caps) | OnePlus Tab 3 landscape: no line > 75 ch; session card ≤ 42 rem; no horizontal scroll; portrait ↔ landscape rotation keeps session state | ✓ |
| M10 | Backups + restore drill (was M8) | `01:412` criteria | ✓ |
| L1 | Listening exercise | `01:413` | later |
| L2 | `council report` + rework loop tooling + style-guide revision process | One rework round imported; report shows per-model acceptance | later |
| L3 | Stats | `01:415` | later |
| L4 | FSRS optimiser (exclude rows with a `card_epoch` older than the card's current epoch) | `01:416` | later |
| L5 | Passkeys | — | later |
| L6 | Optional extra reminder channels: ICS feed, ntfy/HA webhook, reviewer nudge | — | later |

- **Parallelism:** M5 (FSRS) depends only on M2's schema and can run alongside M3/M4.
- The phone layout comes in M4/M6. The wide layout comes for her screens in M4, because she reviews on the tablet too and the grid is that screen's core, and for the learner screens in M9. Every component from M4 on uses container queries, so M9 is composition, not rework.
- **MVP = M0–M10.** The learner can't see anything until M4 has produced approved words, which is the point of the gate.

**Top risks — REPLACES `01:419-426`:**
1. **Her queue gates everything** (no badge bypass anymore).
   - Mitigations: batch-approve on high agreement, 15-item sittings, topic grouping, the owner's runway warning (E.1), and a reviews-only session that is still a valid day.
2. **Council cost/variance or an unusable vendor.**
   - Mitigations: `COUNCIL_MIN_MODELS`, a vendor-diverse roster, versioned prompts, `--dry-run`, and actual costs in the manifest.
3. **OnePlus background delivery** (OEM battery management).
   - Mitigations: the Unrestricted battery setting in the setup checklist, a real-device test in M8, and the in-app badge.
4. **Edge misconfiguration** (forwarded headers / Secure cookie).
   - Mitigations: `SecurePolicy.Always`, the explicit `KnownIPNetworks`, and the M1 edge tests.

---

## ΔH. Privacy — AMENDS `01:273`, `01:282`

- **What may go to third-party models:**
  - word content only: lemma, POS, glosses, examples, notes written *as content*, topic and CEFR;
  - the existing and rejected lexicon list;
  - `style-guide.md`;
  - the JSON schema;
  - for `rework`: the flagged field names and her **structured** edit diffs (content changes).
- **Never sent:**
  - her free-text `note`, her name, either username or display name;
  - user ids, emails, IPs, user agents, push endpoints, timestamps finer than a date;
  - `review_log` or any learner performance data (lapse rates, answers);
  - the `.env`.
  - Not even role labels: council prompts don't mention a reviewer at all.
- **Guard in the CLI:**
  - `tools/council/privacy.local.json` (gitignored) lists strings that must never leave the machine: both real names, usernames, email, the domain.
  - Every outgoing prompt is scanned (case-insensitive, accent-folded). A hit **aborts the run before any network call**.
  - `style-guide.md` and the lint run the same scan in CI, using a committed hash-only list (SHA-256 of normalised strings), so the names themselves aren't in git.
- **Export contents** (`palabras.feedback.v2`):
  - word keys, word content, action, field, chosen alternative, edit diff, `via_batch`, council ids, prompt versions, `agreement_at_review`, `decision_ms`, `created_at` rounded to the date;
  - `reviewer` = the fixed label `native_es_navarra` (`01:273`);
  - notes are excluded unless `includeNotes=true` (owner, local only).
  - **Strip before any external use** (the CLI's `rework --from` does this itself and refuses a file containing `note`): `note`, any `*_id` of users, timestamps finer than a date.
- Provider-side data retention: use API keys (not consumer chat apps). Per-vendor zero-retention or no-training settings are UNVERIFIED and must be checked when the roster is chosen in M3.
- The content itself (Spanish vocabulary) is not personal data. Her edits are content once applied.

---

## ΔI. Obsolete — do NOT build

| Item | Where in 01 |
|---|---|
| iOS: 16.4+ caveat, Home-Screen install path, "actions absent on iOS", Safari install instructions, Background Sync iOS note, "degrades (iOS)" acceptance, top risk #1 iOS push | `01:21`, `01:231`, `01:397-398`, `01:411`, `01:420-421`, README "(iOS/Android)" `01:468` |
| Proxy-detection matrix (NPM/SWAG/Traefik/Caddy/cloudflared), `compose.caddy.yml` fallback overlay, `networks: proxy external`, `PROXY_NETWORK`, `FORWARDED_KNOWN_NETWORK` env | `01:317-332`, `01:349-361`, `01:368` |
| ICS feed as always-on/primary fallback; `FALLBACK_NOTIFY_URL` webhook in MVP (both → L6 optional) | `01:246-248`, `01:368`, `01:411`, `01:421` |
| Last-call reminder, `last_call_time`, 19:00 default, `start`+`skip` action pair, `reminder_log.kind='last_call'` | `01:58`, `01:238-240`, `01:244`, `01:126` |
| Auto-consume skip / `frozen` status / ISO-week skip / "≥1 done per ISO week" streak clause / `day_status.iso_week` | `01:118-120`, `01:174-179`, `01:409`, `01:479-480` |
| Non-stalling rule: `pending_review` words in the learner session with a "sin verificar" badge | `01:262`, `01:425` |
| Free-text → owner "Aplicar" → back to her for re-OK round trip (her edit is now authoritative) | `01:259` |
| Manual "paste JSONL into a Claude/Codex session" content loop and single-LLM drafting | `01:275-280`, `01:304` |
| `words.source='seed-llm'`; `cards.suspended` bool | `01:76`, `01:98` |
| Notes included in export by default | `01:282` |
| Assumption A3 (phone OS unknown) and A2 (Amsterdam timezone) | `01:20-21` |

## ΔJ. Residual open questions
- **None block M1.**
- Non-blocking, resolved by a read-only check in M0: is nginx host-networked or a bridge container? That picks the loopback-port setup (default) or the shared-network variant (A.2).
- Needed by M3, not before: the actual subdomain, and the provider roster plus API keys.

## Evidence check
- Re-read of `01-spanish-pwa-design.md` line refs used above: `:20-21`, `:34`, `:58`, `:66-78`, `:87-91`, `:93-101`, `:108-127`, `:129-135`, `:146`, `:149`, `:168-180`, `:209`, `:220-223`, `:228-249`, `:253-282`, `:287-313`, `:315-398`, `:400-426`, `:456-458`, `:468`, `:471-482`. Each was read in this session and matches the description given here. **verified: re-read OK.**
- External facts:
  - Chrome renders 2 notification actions: developer.chrome.com/blog/notification-actions;
  - Chrome install criteria: web.dev/articles/install-criteria;
  - `KnownNetworks` obsolete in favour of `KnownIPNetworks` (`ASPDEPR005`): learn.microsoft.com, aspnet/core breaking-changes/10/ipnetwork-knownnetworks-obsolete.
- UNVERIFIED items are tagged inline:
  - the container source IP via the loopback bind;
  - OnePlus CSS viewport sizes and OEM battery behaviour;
  - council cost/latency figures;
  - non-Claude model IDs;
  - provider retention settings;
  - whether a closed notification satisfies `userVisibleOnly`.