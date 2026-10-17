# Run reports (`docs/runs/`)

The numbered work log of run `T-20261017-1516` — the planning + scaffolding phase of this repo
(design, M0 scaffold, mock UI, deploy prep). One file per step, named `<step>-<topic>.md`,
written by the agent that performed that step. Step numbers run in execution order; the two
files with prefix 13 are two reports for one step.

Copied verbatim from the run workspace outside the repo. They are frozen **historical
records**, not living documentation: nothing in this folder is kept up to date, and the files
are not edited — including their internal references to the original run workspace (see
"About `.log` citations" below).

## Start elsewhere

Read `../00-handoff.md` first. It is the distilled, corrected state and links into these
reports where evidence is needed. Do not read this folder end to end.

## Reading order (if you need it)

1. `01-spanish-pwa-design.md` — the original design (frozen original of `../design.md`).
2. `02-design-amendment.md` — the ΔA–ΔJ amendment superseding parts of 01 (frozen original of
   `../design-amendment.md`).
3. `03-m0-scaffold.md` — repo, CI, SDK versions, first deploy files.
4. `04-proxy-bind.md` → `05-m0-review.md` → `06-m0-tests.md` → `07-m0-fixes.md` →
   `08-port-18480.md` → `09-m0-final-review.md` → `10-m0-shouldfix.md` — the build/review
   cycles (parts of 04 and 08 are superseded; see below).
5. `13-ui.md`, `13-pwa-ultrawide.md` — the mock UI, PWA shell, ultrawide layout.
6. `16-handoff-docs.md` — the step that wrote the `docs/` set.

## If you read only three

- `03-m0-scaffold.md` — what exists and how it was built.
- `09-m0-final-review.md` — the review findings that drive the M1 edge work
  (`10-m0-shouldfix.md` is its fix list).
- `13-ui.md` — the current frontend surface (routes, mock data, checks).

## Supersession rules

- **A later report supersedes an earlier one.** Each report describes the repo as of its
  step; fixes landed in later steps.
- **Where any report contradicts `../design-amendment.md`, the amendment wins.** In
  particular, report 01's delivery plan is superseded by the ΔG milestone numbering
  (M0–M10 + L1–L6), and 01's proxy/deploy section by ΔA.
- `../00-handoff.md` §6 lists the known disagreements between reports and the current repo,
  with resolutions. Consult it before trusting an older report's claim.

## Known staleness (as of 2026-10-17)

Superseded in whole or in part:

- `04-proxy-bind.md` — its proxy path (loopback bind + private `palabras_net`) is superseded
  by the current compose (`127.0.0.1` publish + external `sieben` network). `08-port-18480.md`
  shares the superseded LAN-publish specifics; its port-18480 decision itself stands.
- `05-m0-review.md` — the first review. Its findings were worked through in 07/09/10; check
  those before acting on 05.
- `06-m0-tests.md` — reports 7 backend tests; the repo now has 8.
- `09-m0-final-review.md` item 13 — its `KnownProxies` suggestion is wrong for the current
  proxy path (handoff §6.2).
- `13-ui.md` — says the `sw.js`/manifest no-cache headers are missing; `Program.cs` now has
  them (handoff §6.4).

Steps after 16:

- Step 18, the first real deploy attempt, left **no report in this folder**. Its outcome: the
  clone on Unraid is blocked awaiting the read-only GitHub deploy key being added to the
  repository (the key file already exists on the server; see the deploy section of the root
  `README.md`). No app container has started, and `https://palabras.siebentower.nl` currently
  returns 502 — expected until the first successful deploy.
- Steps 19 and 20 were still in flight when this README was written. Step 20 is the copy of
  these reports into this folder itself; its own report stays in the run workspace outside
  the repo, so the archive in this folder ends at step 16.

## Steps with no report

| Step | What exists instead |
|---|---|
| 11 | Only `11-impl.log` (docker build, `git grep 8080`, docker credential workaround) — raw log, not copied |
| 12, 14, 15 | Nothing; no report was written |
| 17, 18, 19 | Nothing in this folder as of this README (18 is the deploy attempt described above) |

## About `.log` citations

Raw command-output `.log` files were **not** copied into the repo. Any reference to a `.log`
file — here, in `../00-handoff.md`, or inside the reports themselves — points at the original
run workspace outside the repo, not at a file you can open here. References inside the reports
to `.opencode/runs/T-20261017-1516/…` are frozen historical text, deliberately not rewritten;
the `.md` files they mention sit alongside in this folder.
