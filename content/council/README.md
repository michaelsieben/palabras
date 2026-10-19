# content/council

Committed council bundles. This directory is **data**, not code: it is copied into the runtime image
at `/app/content/council` and read by `ContentSyncService` at startup. Nothing here calls a model.

One subdirectory per run, named by the bundle's `run_id`:

```
content/council/<run_id>/
  manifest.json        # audit record: models asked, failures, counts, candidates_sha256
  candidates.jsonl     # one importable candidate per line
  dropped.jsonl        # audit only, never imported
  raw/                 # gitignored; provider payloads for local debugging, never shipped
```

Rules:

- The contract lives in `content/schema/council-manifest.schema.json` and
  `content/schema/council-candidate.schema.json`; the semantic rules JSON Schema cannot express are
  enforced by `Backend/Palabras/Content/Council/CouncilBundleValidator.cs`,
  `tools/council` (the offline CLI) and `tools/content-lint/candidates.js` (the CI backstop).
- `candidates_sha256` is the SHA-256 of the raw `candidates.jsonl` bytes and is verified before any
  database write.
- `raw/` is never committed and never copied into an image.
- Keep bundles small: `candidates.jsonl` must stay under 2 MB and this whole directory under 10 MB
  (plan §4.3). This ships vocabulary, not a model cache.
- The startup sync imports **propose** bundles only. A rework bundle is applied with
  `dotnet Palabras.dll import-council <dir>`, never by a container restart.

Before the first real run this directory carries only this README and `.gitkeep`, so the Docker
`COPY` and the MSBuild content glob have a directory to point at. The first bundle lands with the
owner-run M3 council run.
