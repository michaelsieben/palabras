# Cookie authentication

Create accounts with the [account CLI](04-accounts.md); no environment/config accounts exist.
`POST /api/auth/login` accepts JSON `username`/`password` and `X-Palabras: 1`.
Success updates `last_login_at`; unknown user and bad password return identical 401 JSON,
both doing PBKDF2-SHA256 verification (600,000 iterations). `GET /api/auth/me` returns
`username`, stored `role`, and `roles`; owner also has the learner authorization role.
`POST /api/auth/logout` clears the cookie. Login normalizes case and surrounding spaces.

Cookie `__Host-palabras`: Secure always, HttpOnly, SameSite=Lax, Path=/, no Domain,
30-day persistent expiry with sliding renewal. Identity uses immutable `users.id`;
each request checks the current row and a password-derived stamp, and refreshes renamed
usernames. `user set-password` or deleting the row revokes existing sessions immediately;
`user reset --yes` also revokes them, including sessions whose numeric id is reused.
Logout clears this browser's cookie; it does not revoke copied cookies on other devices.

CSRF protection adds the binding `X-Palabras: 1` header on **every non-GET /api call**,
including login/logout, beyond SameSite=Lax and JSON POST. No CORS is enabled, so foreign
origins cannot send this custom header without a preflight the app will not authorize.
No separate antiforgery token is needed for this same-origin JSON-only interface.

Only `/healthz`, `/login` (also `/login/`), `/api/auth/login`, and real static assets
(including manifest and service worker, excluding HTML) are public. Anonymous API requests
return 401; page requests redirect to `/login?ReturnUrl=...`, never serve the protected SPA.
Authenticated unknown `/api` routes retain JSON 404; authenticated page routes retain SPA
fallback. Secure cookies work on `http://localhost` in supported browsers and curl; use
localhost in dev, not an arbitrary LAN HTTP address. NPM alone owns HTTPS redirects/HSTS.

Forwarded headers run first, trusting only `172.18.0.0/16` (`sieben`); other peers' forwarded
IP/scheme/host are ignored. Resolved `RemoteIpAddress` is logged before auth/rate limiting.
All containers on this subnet are trusted: keep untrusted containers off it. Allowed hosts
are `palabras.siebentower.nl`, `localhost`, `127.0.0.1`. Login permits **5 POST attempts per
minute per resolved IP + normalized username**, no queue; excess returns JSON 429 and
`Retry-After: 60`. Limits are process-local and reset on restart; changing username starts
another partition, so this is a small private-app control, not global brute-force mitigation.

Production keys persist at `/data/keys` on the cache-pool bind mount, owned by UID 1654;
cookies survive rebuild/restart. Protect keys/backups: keys are not encrypted at rest.
To force everyone out, stop the app, move the entire key ring outside `/data/keys`, create
an empty writable directory owned by 1654, then restart. Never restore the old ring while
old cookies remain valid. Ordinary automatic key renewal does **not** revoke old cookies.

## Verify on the server

Run these **on Unraid**, without changing the real account password. Enter the existing
credentials through stdin (never argv); `curl` consumes JSON from `/dev/stdin`. Clean up
the jar afterward. Root-only temporary files below prevent exposing cookie credentials.

```sh
umask 077
jar=$(mktemp)
printf 'Login JSON (username and password), then Enter: ' >&2
IFS= read -r login_json
printf '%s' "$login_json" | curl -sS -D - -c "$jar" \
  -H 'Content-Type: application/json' -H 'X-Palabras: 1' \
  --data-binary @- http://localhost:18480/api/auth/login
unset login_json
curl -sS -o /dev/null -w 'anonymous page: %{http_code}\n' http://localhost:18480/sesion
curl -sS -o /dev/null -w 'anonymous API: %{http_code}\n' http://localhost:18480/api/ping
curl -sS -b "$jar" http://localhost:18480/api/auth/me
curl -sS -b "$jar" -o /dev/null -w 'authenticated page: %{http_code}\n' http://localhost:18480/sesion
curl -sS -b "$jar" -c "$jar" -X POST -H 'X-Palabras: 1' http://localhost:18480/api/auth/logout
curl -sS -b "$jar" -o /dev/null -w 'after logout: %{http_code}\n' http://localhost:18480/api/auth/me
rm -f "$jar"
```

Expected: login 200 with the cookie attributes above; anonymous page 302/API 401;
`me` includes no hash; authenticated page 200; logout 204; after logout 401.
Do not paste raw Set-Cookie headers/jars into shared logs. A restart-survival check requires
an explicitly authorized `docker restart palabras` between login and authenticated requests.
