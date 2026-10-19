# Open questions

Each item has a recommended default. Apply the default unless the user says otherwise.
Items resolved since `design-amendment.md` ΔJ are listed at the end so they are not reopened.

## Q1. Compose folder on Unraid: `palablras` or `palabras`?

`README.md` said `/mnt/user/docker/palablras/`; `AGENTS.md` says `/mnt/user/docker/palabras/`.
**Resolved (2026-10-17):** `/mnt/user/docker/palabras` was created and the app deployed (`README.md:60-64,71`); a
production incident followed on 2026-10-18 (`08-upgrade-rollback.md:285-289`). The default below was applied.

**Default:** create `/mnt/user/docker/palabras/` and fix the README spelling in the next README
refresh. Consequence: the folder name, compose project (`name: palabras`), container name and
network alias are then all `palabras`, so no name differs between disk and Docker.

## Q2. Where the compose file lives, and whether to rename the container

`deploy/unraid/docker-compose.yml` uses `build.context: /mnt/user/repos/palabras`, so it can run
from any folder. Options: run it in place from the repo clone, or copy it (plus `.env`) into the
docker share.

**Default:** keep a copy of the compose file and the `.env` in `/mnt/user/docker/palabras/` with
`build: /mnt/user/repos/palabras` pointing at the clone. Deploy = `git pull` in the clone, then
`docker compose up -d --build` in the docker share. The `.env` (with `APPDATA_PATH`) stays out
of the repo. Never rename the container: NPM's upstream `http://palabras:18480` resolves through
the `sieben` alias, and a rename means editing the NPM proxy host at the same time.

## Q3. `APPDATA_PATH` is blank in `.env.example`

Compose fails fast (`${APPDATA_PATH:?}`) when it is unset, which is intended.

**Default:** leave `.env.example` blank on purpose and set
`APPDATA_PATH=/mnt/cache/appdata/palabras` in the server `.env`. Add a comment in
`.env.example` saying "must be a cache/pool path, never /mnt/user" at the next edit by its owner.

## Q4. Trusting the whole `sieben` network

`KnownIPNetworks = 172.18.0.0/16` trusts `X-Forwarded-*` from every container on `sieben`, not
only NPM. NPM's address on `sieben` is not fixed (currently `172.18.0.37`).

**Default:** accept the CIDR for MVP (single household, containers under the user's control).
Later options: give NPM a static IP on `sieben` and switch to `KnownProxies` with that IP, or
create a dedicated `palabras_edge` network with only NPM and `palabras` on it.

## Q5. `runs/09` item 13: `KnownProxies = 192.168.68.2`

That is NPM's macvlan `br0` IP. With the current `sieben` path the app never sees that address.

**Default:** ignore item 13 and use Q4. Revisit only if NPM is switched to proxy via
`192.168.68.66:18480` (which the current `127.0.0.1` publish does not allow).

## Q6. Bare `/api` response

`runs/07-m0-fixes.md` saw 404; `runs/09-m0-final-review.md` says it falls through to the SPA.

**Default:** `/api` and `/api/*` both return 404 JSON, never `index.html`. Add a test in M1.

## Q7. Cache headers for `index.html` and manifest MIME type

`Program.cs` sets no-cache on `sw.js` and `manifest.webmanifest` (no report). `index.html` is not
no-cache; the `Content-Type` of `manifest.webmanifest` is unverified.

**Default:** serve `index.html` with `Cache-Control: no-cache` (including from the SPA fallback)
and `manifest.webmanifest` as `application/manifest+json`; cover both in tests during M1.

**Resolved:** applied in `Program.cs:192,199-201,231` (see `00-handoff.md` §6.4).

## Q8. Unreported 8th backend test

`M0ApiTests.cs` has 8 tests; the last reports say 7.

**Default:** run `dotnet test` at the start of M1 and record the count in that run's report.

**Resolved:** `M0ApiTests.cs` now has 9 tests; the full suite passes (825/0 at `83d29a5`, `00-handoff.md` §6.3).

## Q9. Content schema typo

`content/schema/word.schema.json:68-69` defined the generic-Spain scope as `"paninsular-generic"`,
and most word files in `content/words/` used it as `region.scope`, so they linted clean against a
misspelt value that is not a word.

**Resolved (T-20261017-1516):** renamed to `"peninsular-generic"` in the schema, in every word file
and in the two word files whose notes named the scope, in one change, with lint re-run. Kept as a
record: after the first import, a further rename needs a data migration too.

## Q10. Content lint not in CI

**Default:** add the step from the `tools/content-lint` README to `.github/workflows/ci.yml` in M2.

**Resolved:** `Lint content` step at `.github/workflows/ci.yml:38-39`.

## Q11. Real-device viewport and battery behaviour

CSS viewport sizes of the OnePlus 15 and OnePlus Tab 3 and OxygenOS battery restrictions on
Chrome push are unverified (`design-amendment.md` evidence check, `runs/13-pwa-ultrawide.md`).

**Default:** open `/debug/viewport` on both devices after the first deploy, record the numbers
in `runs/`, and adjust breakpoints only if they differ. For push, exempt Chrome from battery
optimisation on the phone before testing M8.

## Q12. Council providers and keys (needed by M3)

Provider roster, non-Claude model IDs, API keys, retention settings and cost/latency are open
(`design-amendment.md` ΔJ and evidence check).

**Default:** decide at M3 start; keys live in a local untracked `.env` for `tools/council`, never
in the container (no LLM at runtime).

**Status (M3 C7, `a3cdfff`):** the file is `tools/council/.env.council` (template `env.example`;
`tools/council/README.md` Configuration). The roster, keys and retention settings are still open and
are decided by the owner at the first real run (C9); the model ids in `env.example` are placeholders.

## Q13. Does a dismissed notification satisfy `userVisibleOnly`?

Unverified (`design-amendment.md` evidence check).

**Default:** always show a visible notification for every push (including the action
confirmations); test on the OnePlus 15 in M8.

## Q14. Stale `README.md`

It describes a placeholder screen and the old milestone numbering.

**Default:** whoever next owns `README.md` updates it to match `00-handoff.md` §4 and ΔG; link
to `docs/` rather than duplicating it.

## Resolved (do not reopen)

- ΔJ "nginx host-networked or bridge?" → bridge; NPM is on macvlan `br0` and on `sieben`; app
  joins `sieben` (`deploy/unraid/docker-compose.yml`).
- ΔJ "actual subdomain" → `palabras.siebentower.nl`.
- Port → 18480 everywhere (`runs/08-port-18480.md`).
- Repo visibility and deploy source → private GitHub repo, cloned to `/mnt/user/repos/palabras`.
