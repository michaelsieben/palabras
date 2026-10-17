# Accounts: create and manage without a restart

Push/deploy this milestone first. The Unraid poller watches `origin/main` every five minutes;
wait for a successful rebuild in `/mnt/user/docker/palabras/deploy.log` and a healthy container.
Then run on the Unraid terminal (replace the example usernames):

```sh
docker exec -it palabras /app/Palabras user create owner --username your-name
docker exec -it palabras /app/Palabras user create reviewer --username reviewer-name
docker exec -it palabras /app/Palabras user list
```

Each create command asks for the password twice, with input hidden. Passwords must be
12-1024 characters. Use a long, unique passphrase. Never use a `--password` argument: it would
expose the password in shell history and process lists. For scripting, `--password-stdin`
reads one line from a secure stdin source (use `docker exec -i`, not `-it`).

Exactly one account per role (`owner`, `reviewer`) is allowed. Login names are trimmed,
lowercased, case-insensitively unique, and must match `[a-z0-9._-]{3,32}`.
Nothing is seeded from `.env`; `.env` holds configuration only, never account credentials.

Any time later, no file edits or restart:

```sh
docker exec -it palabras /app/Palabras user set-username your-name --username new-name
docker exec -it palabras /app/Palabras user set-password new-name
docker exec -it palabras /app/Palabras user list
```

Renaming preserves the same account id, creation time, password and all linked history.
Password changes update the stored salted hash immediately.

## Reset the database

**Warning: reset permanently destroys ALL database data, including both accounts, review
history, SRS progress, reviewer feedback and sessions. Back up first if anything matters.**
It recreates an empty schema in the same SQLite file; no container restart is needed.
Database clients may fail briefly during the schema rebuild. It does not delete `/data/keys`
or backup files; resetting existing sessions/cookies belongs to the next auth milestone.

```sh
docker exec -it palabras /app/Palabras user reset --yes
docker exec -it palabras /app/Palabras user list
# Re-run both create commands above to recreate the accounts.
```

Without `--yes`, reset refuses and changes nothing. A missing database is initialized on
startup or the first CLI command. SQLite runs in WAL mode. Default path is
`DATA_DIR` (default `/data`) + `/palabras.db`; `ConnectionStrings__Default=Data Source=...`
can override it (relative paths resolve under `DATA_DIR`). On Unraid `/data` is the existing
`/mnt/cache/appdata/palabras` bind mount, writable by UID 1654; never put SQLite under `/mnt/user`.

`/app/Palabras` is the Linux apphost published in the image. The equivalent fallback is
`docker exec -it palabras dotnet /app/Palabras.dll user list`.

This step supplies persistence and the CLI only. Existing configuration-backed web auth is
not changed here; the next step connects login/logout and route protection to these accounts.
