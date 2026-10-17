# 03 · Auth bootstrap — how the two accounts are created and renamed

Status: **decided**, 2026-10-17 (`T-20261017-1516` step 25). Scope: M1. Read with `00-handoff.md`
§3 and §8 and `design.md` §5. It is an ADR for two questions: where do the first two accounts come
from, and can a login name change afterwards?

This file was first written at step 24, when the answer was "the CLI verb `set-password`". The user
then added one requirement — **the username must be changeable too** — so the verb list is now a
`user` command group with `create` as the only creation path and `set-username` added. Where this
text contradicts the step-24 version, this text wins.

Implementation update (step 29): SQLite accounts and cookie auth shipped in `b4fa213`.
Context below records the step-25 snapshot; references to "today", configuration-backed auth,
missing tables and future M1 work are historical, not the current deployment state.
Current operational contracts are [04-accounts.md](04-accounts.md) and
[05-auth.md](05-auth.md): `/` redirects anonymous visitors to public `/login`, APIs return 401,
and accounts must be created by the user. The published Linux apphost is `/app/Palabras`;
`dotnet /app/Palabras.dll` is an equivalent fallback. CLI examples below use the shipped syntax.

## 1. Context

- **What exists today.** M1 cookie auth landed in commit `05d5fc2` (`Program.cs`,
  `Backend/Palabras/Auth/`): `__Host-palabras` cookie, forwarded headers, roles, CSRF header. Accounts
  are read from **configuration** — `Auth:Users` entries of `Username` / `PasswordHash` / `Roles`
  (`Program.cs:42`, `Backend/README.md:3-5`) — and live in an in-memory `SeedUserStore`. There is no
  `users` table, no DB, no CLI verb, and no login name that can be changed without editing the
  environment and restarting.
- **There is no usable account in production.** The deployed container has no `Auth:Users` at all, so
  every login fails, while the SPA shell is served to anyone. Checked live 2026-10-17: `/healthz` 200,
  `/` 200, `/login` 200, `/api/ping` 200, `/api/review/queue` 401. API routes are gated; the shell is
  not.
- **Exactly two accounts, no registration.** The **owner** = the learner; the **reviewer** = his
  fiancée, who has final authority over every word, sentence and translation (`design.md:211`,
  `00-handoff.md` §2, ΔD approved-only gate).
- **Two requirements drive the decision** (user, 2026-10-17): account management must not require
  editing files on the server or restarting the container, and the login name must be changeable at
  any time.
- `design.md:212-214` already sketches both a first-boot env seed and a `set-password` CLI verb;
  `design.md:469` lists the main binary's verbs (`import`, `export-feedback`, `set-password`,
  `gen-vapid`).

## 2. Decision — a `user` command group on the main binary

Accounts are managed by a CLI command group on the main binary, **run inside the running container**:
no restart, no rebuild, no env change.

| Verb | Behaviour | Name source |
| --- | --- | --- |
| `user create <role> --username <name>` | Creates the one account for `<role>` (`owner`\|`reviewer`), prompts for the password twice, and fails if that role already exists. | **New.** `design.md` has no create verb; it seeded both accounts instead (`:212`). |
| `user set-username <old> --username <new>` | Renames the login name. One `UPDATE`, no password prompt, no other change (§3). | **New** — this is the user's added requirement. |
| `user set-password <username>` | Prompts for a new password (twice), replaces the hash. | Verb name from `design.md:214` ("Reset uses the CLI `docker exec palabras dotnet Palabras.dll set-password <user>` (reads from stdin)") and `design.md:469`. The `user` grouping and the `<username>` argument spelling are new. |
| `user list` | Prints role, username, `created_at`, last login for the ≤ 2 accounts. | **New.** |

Rules that make the group usable:

1. **Passwords are read from the terminal (stdin/tty) twice, never from argv.** argv is disclosed by
   `docker inspect` and the process list; the existing `--hash-password` verb already works this way
   (`Program.cs:9-15`, `Backend/README.md:16`).
2. **`user create` is the only creation path.** `set-password` on an unknown username is an error that
   points at `user create`. *(Supersedes step 24's "`set-password` is an upsert": with an explicit
   create verb, silently resurrecting a mistyped name would hide the typo instead of reporting it.)*
   `user create` also takes the role, *(superseding step 24's "the verb takes no role argument")*:
   creation is the one place the role is decided, and the pair is fixed by `design.md:52`
   (`role CHECK(role IN ('owner','reviewer'))`).
3. **`<role>` is the account role `owner`\|`reviewer`**, not an ASP.NET policy name. It maps to the
   claims of `design.md:215-216`: owner → `Learner` + `Admin`, reviewer → `Reviewer`. Usernames are
   independent of roles: `owner` and `reviewer` are only the defaults in `.env.example:7-10` and
   `design.md:212`, and a rename never touches the role. The current config surface accepts only the
   ASP.NET role names (`Program.cs:46`) and reconciles both into one array; the DB `role` column
   replaces that.
4. **Hash:** `PasswordHasher<T>` (PBKDF2) stored in the `users` row, plaintext never persisted —
   `design.md:37`, `design.md:208`, `00-handoff.md` §3. *(The step-25 briefing said Argon2id; that is
   not the design. Changing the KDF is its own decision and is not made here.)*
5. **Supersedes `design.md:212-213`** — the first-boot seed of both accounts from `OWNER_USERNAME` /
   `OWNER_INITIAL_PASSWORD` / `REVIEWER_USERNAME` / `REVIEWER_INITIAL_PASSWORD`. §5 keeps the only
   seed-shaped mechanism we would accept, and only if the user asks for it.
6. **Retires the `Auth:Users` configuration surface** as the account source for production
   (`Program.cs:42-68`, `Backend/README.md`). The hash-in-config flow (`--hash-password` → paste the
   hash into the environment) is superseded by `user set-password`; whether the dev-only verb is kept
   for local convenience is a mechanical call for the M1 implementer.
7. **Dependency:** the verbs need a `users` table, which does not exist yet (the store is in memory).
   M1 must ship the `users` table (schema per `design.md:51-54` plus the timezone columns of
   `design-amendment.md` ΔF:545-547) or the verbs land with the first EF Core migration in M2.
   Recorded as a sequencing dependency; not resolved here.

In-container form: container `palabras`, published apphost `/app/Palabras`, image `WORKDIR /app`.

```bash
# Replace placeholders with your chosen usernames; passwords are prompted, never argv.
docker exec -it palabras /app/Palabras user create owner --username <name>
docker exec palabras /app/Palabras user list

docker exec palabras /app/Palabras user set-username <old> --username <new>
docker exec -it palabras /app/Palabras user set-password <new>
```

`exec` runs against the live container and the live database, so no `docker compose up` is involved.
The published apphost is present even though the image's web entrypoint uses `dotnet Palabras.dll`.

## 3. The username decision

**The login name is mutable at any time.** The rule that makes that safe:

- The `users` table has a **surrogate primary key `id`** (`design.md:52`: `id PK`). `username` is
  `UNIQUE NOT NULL` but is an ordinary mutable column, not a key.
- **Every foreign key points at `id`, never at the username**: `user_settings.user_id`
  (`design.md:56`), `cards.user_id` (`:94`), `practice_sessions.user_id` (`:104`), `day_status.user_id`
  (`:118`; `day_skips.user_id`, ΔF:636), `push_subscriptions.user_id` (`:123`), `reminder_log.user_id`
  (`:126`), `word_reviews.reviewer_id` (`:130`).
- Renaming is therefore **a single `UPDATE users SET username = <new> WHERE id = <id>`**. It must never
  create a second row and must never orphan history.
- **Explicitly forbidden:** `username` as a primary key; `username` as a foreign key or
  `REFERENCES users(username)`; and any code path that "recreates" the account (delete + insert) to
  rename it — that would mint a new `id` and detach every row that references the old one.

Consequences to keep:

- **The fiancée's accumulated feedback survives a rename.** Her verdicts are `word_reviews` rows keyed
  by `reviewer_id`, so they stay attached; nothing in the review history stores a login name. The only
  username-shaped string in the schema is `word_revisions.created_by`, a fixed label
  (`'import'|'council_rework'|'reviewer'|'owner'`, ΔF:571), which is not the login name. The council
  export likewise writes the role label `reviewer`, never a name or username (`design.md:273`,
  `design-amendment.md:708,714`), so a rename cannot leak into or invalidate provenance.
- **A rename must not log anyone out.** The cookie ticket keys on the user **`id`** (the identity
  claim); the role claim carries the role; the display name is cosmetic. So a rename leaves both
  sessions valid and there is nothing username-derived in a cookie to go stale. *(Today the ticket sets
  `ClaimTypes.Name = user.Username` with no id, `AuthEndpoints.cs:18`, and `/api/auth/me` reports the
  identity name, `:32`; with a table, the identity claim must be the id.)*
- **Normalization rules**, applied by the CLI, the login endpoint and the unique constraint alike:
  - trimmed, stored **lowercase**; uniqueness and login matching are **case-insensitive**;
  - allowed characters and length: `[a-z0-9._-]{3,32}`; anything else is rejected with a clear message;
  - SQLite's plain `UNIQUE` is case-sensitive, so the constraint must use `NOCASE` collation (or a
    unique index on `lower(username)`), otherwise `Owner` and `owner` both insert. The current login
    lookup compares with `StringComparer.Ordinal` (`SeedUserStore.cs:29`), which must become
    case-insensitive;
  - the login form is **username + password**. There is no email, no display name in the login path,
    and `display_name` (`design.md:52`) stays free-form and is never the login name.
- The rename verb takes no password and changes no role: one verb, one `UPDATE`.

## 4. Why not `.env` + restart (the user's proposal, recorded)

The proposal was to put the initial password in `.env` and restart the container. Rejected:

- **A secret would live on the server.** The deployed `.env` sits beside the compose file at
  `/mnt/user/docker/palabras/.env` (compose `env_file: .env`, `deploy/unraid/docker-compose.yml:19`;
  gitignored and `chmod 600`, but still a readable file that is also copied through the repo
  checkout).
- **Every change needs a restart.** Configuration is read at startup (`Program.cs:42`), so each
  password change is a `docker compose up -d` on a live app.
- **Seed-on-boot must be idempotent or it overwrites a later change.** "Create if the table is empty,
  then ignore" (`design.md:213`) is the only safe shape, and it leaves the stale plaintext in the file
  forever with a startup warning as the only reminder. (Today's config path only survived because
  plaintext passwords are refused outside Development — `Program.cs:52-55` — and a hash has to be
  pasted in by hand.)
- **It has no answer at all for "rename my account".** `OWNER_USERNAME` is read at boot
  (`.env.example:7`, `design.md:212`); changing it either does nothing, or the boot logic "fixes" the
  name and rewrites the row on every restart — which is a restart-driven rename, exactly what was
  asked not to be needed.

What the CLI gives instead:

- A salted password **hash** is the only thing written, to SQLite under `/data`; the plaintext never
  leaves the terminal.
- **No restart, no rebuild**: the verb runs in the live container against the live database.
- Password, login name and role are **separate verbs** (`set-password`, `set-username`, `create`), so
  each change stays small and none of them can rewrite another by accident.

## 5. Optional zero-touch first boot (not M1, only if the user asks)

If an unattended first start is ever wanted, **gate it — do not seed it**:

- If and only if the `users` table is empty, accept a one-time `INIT_TOKEN` from the environment and
  create both accounts from `OWNER_USERNAME`/`REVIEWER_USERNAME` plus the token as the shared initial
  password.
- Immediately after creating them the token is **ignored** for the rest of the process lifetime, and
  startup logs a reminder to delete that line from `.env`.
- The token is useless once the table is non-empty, so there is no permanent secret and no restart.

Marked **optional**: not part of M1 unless the user asks for it. §2's `user create` commands are the M1
flow.

## 6. What `.env` is for

Configuration only, never credentials:

- `APPDATA_PATH` → `/data` bind mount (`/mnt/cache/appdata/palabras`; never `/mnt/user`, FUSE breaks
  SQLite WAL locking — `00-handoff.md` §3, `.env.example:20-23`).
- `APP_PORT` (18480, `deploy/unraid/docker-compose.yml`), `TZ` (Europe/Madrid; container clock only, `design-amendment.md` ΔA).
- `APP_BASE_URL` (used once reminders and Web Push exist — ΔG M8).
- Cookie/session settings: the cookie name is fixed as `__Host-palabras` (`00-handoff.md` §8.5,
  `design.md:209`). Because of the `__Host-` prefix the cookie must carry `Secure`, `Path=/` and
  **no `Domain` attribute**, so a "cookie domain" setting must not be introduced; a session/lifetime
  knob (sliding 30 days, `design.md:209`) is legitimate configuration.
- `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` / `VAPID_SUBJECT` and `FALLBACK_NOTIFY_URL` for M8;
  `BACKUP_RETENTION_DAYS` for M10; `FORWARDED_KNOWN_NETWORK` for the M1 edge (`172.18.0.0/16`).

Current env surface: `.env.example` (`00-handoff.md` §6.7 on the stale README). To drop when M1 lands:
`OWNER_INITIAL_PASSWORD` / `REVIEWER_INITIAL_PASSWORD` (`.env.example:8,10`) — the rejected seed path —
and the production `Auth__Users__*` surface (`Backend/README.md`). `OWNER_USERNAME` / `REVIEWER_USERNAME`
survive only as §5's optional defaults. These files are not owned by this task; the notes stand as
follow-ups. The deployed `.env` lives only on the server, never in git (`.gitignore` excludes `.env`).

## 7. Consequences for M1's "done when"

- **A fresh database has no usable account**: with an empty `users` table, no login succeeds until
  `user create` has been run for that role. Today the only account source is `Auth:Users` config, which
  the deployment does not set.
- **Unauthenticated requests are refused**: any request to a non-public route without a valid session
  cookie returns 401 (API) or redirects to `/login` (SPA) — it does **not** render the app. Today `/`
  and `/login` return 200 to anyone, as recorded in §1.
- **A rename preserves history**: after `user set-username`, the row's `id` is unchanged, every FK row
  still joins, and the case-insensitive unique constraint still holds (`owner` cannot be renamed to
  `Owner` on a second account). Test it against a DB that already has review history.
- **At most two accounts**, roles `owner` and `reviewer`, and **no public registration route**
  (`design.md:211`); `user create` refuses a role that already exists.
- **A rebuild changes no account**: data protection keys persist (rule 6 of `00-handoff.md` §8) and
  tickets key on `id`, so a container rebuild logs nobody out and renames nothing.

Practical sequence for the user once M1 ships:

```bash
# deploy (main-branch poll, or `docker compose up -d --build` in /mnt/user/docker/palabras)
docker exec -it palabras /app/Palabras user create owner --username <owner-name>
docker exec -it palabras /app/Palabras user create reviewer --username <reviewer-name>
# then log in at https://palabras.siebentower.nl/login

# any time later, no restart:
docker exec palabras /app/Palabras user set-username <old> --username <new>
docker exec -it palabras /app/Palabras user set-password <new>
docker exec palabras /app/Palabras user list
```

Acceptance lines above belong in `01-next-steps.md` §1 alongside the existing M1 "done when" text.
