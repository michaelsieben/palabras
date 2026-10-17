# 16 — handoff docs (docs-writer)

STATUS: ok. No git commands run; `Palabras/docs/**` left uncommitted.

## Files written (all new, `Palabras/docs/`)

| File | Lines | Content |
| --- | --- | --- |
| `design.md` | 495 | Verbatim copy of `01-spanish-pwa-design.md` |
| `design-amendment.md` | 760 | Verbatim copy of `02-design-amendment.md` (written in 3 chunks; tail verified) |
| `00-handoff.md` | 174 | Entry point: docs index, project in user terms, locked decisions, exists / missing (each cited), source disagreements, ΔG milestone table, M1 sequencing constraints, per-subsystem reading list |
| `01-next-steps.md` | 117 | Ordered queue with "done when": M0 deploy, M1, M2, M5, M6 (+M7/M9), M3+M4, M8, M10, L1–L6, housekeeping |
| `02-open-questions.md` | 121 | Q1–Q14 with defaults, plus resolved list |

No file outside `docs/` touched. README.md and AGENTS.md read only.

## Disagreements flagged (00-handoff §6)

1. Proxy path: ΔA loopback/palabras_net vs 04/08 LAN 0.0.0.0 vs repo (127.0.0.1 + sieben). Repo authoritative.
2. Trusted proxy: 09 item 13 `KnownProxies=192.168.68.2` vs README `KnownIPNetworks 172.18.0.0/16`. Docs choose the CIDR (the task's "KnownProxies/sieben CIDR" is implemented as KnownIPNetworks since KnownProxies takes IPs only).
3. Test count: reports 7, repo 8 (`Backend/Palabras.Tests/M0ApiTests.cs`), 8th unreported.
4. Report 13 says sw/manifest no-cache missing; Program.cs now has it (no report).
5. Bare `/api`: 07 = 404, 09 = SPA fallback.
6. Compose folder spelling palablras (README) vs palabras (AGENTS.md); neither exists on server.
7. README stale (placeholder screen, old milestone numbers).

## Gaps / unverified

- Server-side "folder does not exist" and "nothing deployed" taken from brief/prior run state, not re-checked over SSH (no shell in this step).
- 8th backend test pass status unknown.
- Runs 12, 14, 15 left no report; 11 only a log.
- Schema typo `paninsular-generic` is used by most word files, not only the schema; Q9 default renames schema + files together before first import.
- Run reports live outside the repo; doc citations use `runs/NN-*.md` relative to the workspace `.opencode` folder, not resolvable on GitHub.
- README refresh needed (owned by other agents).

## Next

implementer — M0 deploy (01-next-steps §0) then M1 forwarded headers + auth per 00-handoff §8.
