# Council bundle fixtures (shared contract)

One directory per bundle. `cases.json` lists every fixture with the verdict both implementations
must reach (`valid`) and the rule it exercises (`code`).

- C# half: `Backend/Palabras.Tests/M3CouncilBundleTests.cs` (T-4, M3b C2).
- TypeScript half: `tools/council/test/validate.test.mjs` (M3 C6). Read `cases.json`, run the CLI
  validator over each directory, and assert the same accept/reject verdict. Do not fork the set.

The bundles were produced by a throwaway generator: one valid base bundle, then one planted defect
per case. Edit a fixture only by regenerating `candidates.jsonl` **and** its `candidates_sha256`,
otherwise a case stops being invalid for the reason it documents.

Fixture grammar: `manifest.json` follows `content/schema/council-manifest.schema.json`,
`candidates.jsonl` lines follow `content/schema/council-candidate.schema.json`. Rules that JSON
Schema cannot express (per-sense alternative counts, band/agreement, run id agreement, resolvable
`target_key`, model ids declared in the manifest) are listed in the plan §3.1 and implemented in
both validators.
