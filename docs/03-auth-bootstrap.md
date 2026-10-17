# 03 · Auth bootstrap — how the first accounts are created

Status: **decided**, 2026-10-17 (`T-20261017-1516`). Scope: M1. Read with `00-handoff.md` §8 and
`design.md` §5. It is an ADR for one question: where do the first two accounts come from?

## 1. Context

- Auth does not exist yet: no `users` table, no `POST /api/auth/login`, no session cookie, no auth
  middleware (`00-handoff.md` §5). The deployed shell is therefore **unauthenticated** and already
  public: nginx-proxy-manager serves `https://palabras.siebentower.nl/` → `http://palabras:18480`
  (`deploy/unraid/docker-compose.yml`, `README.md`). Verified 2026-10-17 from outside the LAN:
  `/healthz` → 200 and `/` → 200 with no credentials.
- Exactly two accounts, no registration flow (`design.md` `:211`): the **owner** = the learner, and
  the **reviewer** = his fiancée, who has final authority over every word, sentence and translation
  (`00-handoff.md` §2, ΔD approved-only gate).
- `design.md` `:212-214` already sketches both a first-boot env seed **and** a `set-password` CLI
  verb. This file picks the CLI and records why.

## 2. Decision

Accounts are created by a **CLI verb on the main binary, run inside the running container**. No
restart, no rebuild, no env change:

```bash
docker exec -it palabras dotnet Palabras.dll set-password owner
docker exec -it palabras dotnet Palabras.dll set-password reviewer
```

Names and form taken from, not invented:

| Element | Source |
| --- | --- |
| Verb `set-password <user>` on the main binary | `design.md` `:214` ("Reset uses the CLI `docker exec palabras dotnet Palabras.dll set-password <user>` (reads from stdin)") and `design.md` `:469` (verb list: `import`, `export-feedback`, `set-password`, `gen-vapid`) |
| Binary `Palabras.dll` behind `dotnet`, `WORKDIR /app` | `Dockerfile` `ENTRYPOINT ["dotnet", "Palabras.dll"]`; the absolute form `docker exec palabras dotnet /app/Palabras.dll set-password owner` is equivalent |
| Container name `palabras` | `deploy/unraid/docker-compose.yml` (`container_name: palabras`) |
| Usernames `owner`, `reviewer` | `.env.example` `OWNER_USERNAME=owner`, `REVIEWER_USERNAME=reviewer`; `design.md` `:212` |

Requirements this puts on the M1 implementation:

1. `set-password <user>` is an **upsert**: it creates the user row when it is missing and replaces
   the hash when it exists. That is what makes it the creation path; a reset-only verb leaves a
   fresh database with no way in.
2. `<user>` is the **username** (`owner`, `reviewer`), not the role. Roles stay as `design.md`
   `:215-216` defines them (owner → Learner + Admin, reviewer → Reviewer); the verb takes no role
   argument.
3. The password is read from **stdin**, never from argv: argv is disclosed by `docker inspect` and
   the process list.
4. Hash: `PasswordHasher<User>` (PBKDF2) stored in the `users` row, plaintext never persisted —
   `design.md` `:37`, `:208`, `00-handoff.md` §3. *(The `T-20261017-1516` briefing said Argon2id;
   that is not the design and was not written in as if it were. Changing the KDF is its own
   decision, PBKDF2 is what is locked today.)*
5. **Supersedes `design.md` `:212-213`** — the first-boot seed of `owner`/`reviewer` from
   `OWNER_INITIAL_PASSWORD` / `REVIEWER_INITIAL_PASSWORD`, then ignoring them. Plaintext passwords
   in the environment are not a mechanism we keep; §4 below is the only gate we would accept, and
   only if the user asks for it.
6. Follow-up for the owner of `.env.example`: drop the `OWNER_INITIAL_PASSWORD` /
   `REVIEWER_INITIAL_PASSWORD` placeholders once M1 lands, so the rejected path is not offered.

## 3. Why not `.env` + restart (the user's proposal, recorded)

The proposal was to put the initial password in `.env` and restart the container. Rejected:

- **A plaintext password would live on the server.** The deployed `.env` sits beside the compose
  file at `/mnt/user/docker/palabras/.env` (`README.md`, deploy section; `chmod 600` and out of git,
  but still a readable file, and it is also copied through the repo checkout). Design says
  credentials belong in env only as a one-shot seed, which is exactly the fragile part.
- **Changing a password would need a restart.** The `.env` is read at container start, so every
  change is a `docker compose up -d` on a live app.
- **Seeding logic must be idempotent or it destroys a later change.** "Seed only if `users` is
  empty, then ignore" (`design.md` `:213`) is the only safe shape, and it still leaves the stale
  plaintext in the file forever, with a startup warning as the only reminder.

What the CLI gives instead:

- A salted password **hash** is the only thing written, to SQLite under `/data`; the plaintext
  never leaves the terminal.
- **No restart, no rebuild**: the verb runs in the live container against the live database.
- Changing a password is just another command (`set-password owner` again), for either account,
  without touching anything a user changed in the app.

## 4. Optional zero-touch first boot (not M1, only if the user asks)

If an unattended first start is ever wanted, **gate it — do not seed it**:

- If and only if the `users` table is empty, accept a one-time `INIT_TOKEN` from the environment and
  create both accounts from `OWNER_USERNAME`/`REVIEWER_USERNAME` plus the token as the shared initial
  password.
- Immediately after creating them the token is **ignored** for the rest of the process lifetime, and
  startup logs a reminder to delete that line from `.env`.
- The token is useless once the table is non-empty, so there is no permanent secret and no restart.

Marked **optional**: not part of M1 unless the user asks for it. §2's two `set-password` commands are
the M1 flow.

## 5. What `.env` is actually for

Configuration only, never credentials:

- `APPDATA_PATH` → `/data` bind mount (`/mnt/cache/appdata/palabras`; never `/mnt/user`, FUSE breaks
  SQLite WAL locking — `00-handoff.md` §3, `.env.example`, `README.md`).
- `APP_PORT` (18480, `deploy/unraid/docker-compose.yml`), `TZ` (Europe/Amsterdam).
- `APP_BASE_URL` (used once reminders and Web Push exist — ΔG M8).
- Cookie/session settings: the cookie name is fixed as `__Host-palabras` (`00-handoff.md` §8.5,
  `design.md` `:209`). Because of the `__Host-` prefix the cookie must carry `Secure`, `Path=/` and
  **no `Domain` attribute**, so a "cookie domain" setting must not be introduced; a session/lifetime
  knob (sliding 30 days, `design.md` `:209`) is legitimate configuration.
- `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` / `VAPID_SUBJECT` and `FALLBACK_NOTIFY_URL` for M8;
  `BACKUP_RETENTION_DAYS` for M10; `FORWARDED_KNOWN_NETWORK` for the M1 edge (`172.18.0.0/16`).

Current env surface: `.env.example` (`00-handoff.md` §6.7 on the stale README, `design.md` `:368`).
Deployed `.env` lives only on the server, never in git (`.gitignore` excludes `.env`).

## 6. Consequences for M1's "done when"

Acceptance lines to add to `01-next-steps.md` §1:

- **A fresh database has no usable account**: with an empty `users` table, no login succeeds until
  `set-password` has been run for that user.
- **Unauthenticated requests are refused**: any request to a non-public route without a valid
  session cookie returns 401 (API) or redirects to `/login` (SPA) — it does **not** render the app.
  Today `/` returns 200 to anyone, as recorded in §1.

Practical sequence for the user once M1 ships:

```bash
# deploy (auto-deploy poll, or up -d --build in /mnt/user/docker/palabras)
docker exec -it palabras dotnet Palabras.dll set-password owner
docker exec -it palabras dotnet Palabras.dll set-password reviewer
# then log in at https://palabras.siebentower.nl/login
```
