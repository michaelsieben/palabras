# T-20261017-1516 · Step 13 — PWA shell and ultrawide layouts

## Status

**Frontend implementation and all local checks pass. Production acceptance blocked on app-host headers outside assigned ownership.** `Backend/Palabras/Program.cs:9` still calls plain `app.UseStaticFiles()`; no backend `Cache-Control`/`no-cache` rule was found. This step owns `Frontend/**` only, so backend was not modified. `Frontend/HOSTING.md` provides exact integration instructions. Backend owner must serve `/sw.js` and `/manifest.webmanifest` with `Cache-Control: no-cache` **from ASP.NET**, not nginx, and verify both directly and through HTTPS proxy. Confirm manifest MIME type while doing this.

No commit made; frontend deliberately left dirty for orchestrator's selective staging. Generated PNG icons and install screenshots are present under `Frontend/public/`, ready to commit. No edits to content, Docker, CI, root README or backend. Run artifacts are under workspace `.opencode/runs/`, where supplied design files actually live; they were not under Palabras `.opencode/`.

## Inputs and scope

Read repo root `AGENTS.md` (no `Frontend/AGENTS.md` exists), original design §4/§7/§9 and amendment ΔB/ΔE. Preserve existing scaffold's warm paper/green palette, system typography, React 19 strict TS, Vite and plain CSS; no UI-library migration or backend/domain refactor. Android Chrome only. No iOS paths.

`Frontend/src/mock/demo.ts` explicitly isolates mock data. All learner words have approved status; pending queue items never appear in learner views. No API/ping requests, credentials, account logic, FSRS, real streak mutation, offline answer persistence, IndexedDB, background sync or reminders. Prominent Spanish demo label states that changes are not saved. These are shell interactions, not delivery of future learning/reviewer milestones.

## Screens and interactions

- `/` and `/sesion`: learner session with ES→EN card, reveal, example/translation/region note and daily-summary/streak strip. Four direct grading controls: Again/Hard/Good/Easy. Again and Good occupy first phone row; Hard/Easy explicit second row, no inaccessible long-press dependency. Wide session container uses one four-button row. Bottom-sticky phone actions sit above bottom tabs. Demo ends with **Bien hecho**, with repeat action.
- `/palabras`: approved mock word list with ES/EN search, topic filter and stage chips. At ≥1280 the same list appears beside session.
- `/revisar`: reviewer queue, **Vale ✓ / No ✗ / Editar ✎**. Reject opens pre-generated ranked alternatives with **Traducción / Ejemplo / Variante / Palabra** field controls; picking applies local mock change and approves. Explicit **Rechazar palabra** or **Ninguna vale… Cambiar ⚑** with field flags and optional note. Edit-in-place covers lemma, gloss, accepted answers, example ES/EN, note, article/gender. Original values struck through and exposed as field descriptions. Save approves local mock only.
- `/aprobadas`: searchable approved example selection and local **Revisar otra vez** action. No SRS/card suspension is implied.
- `/ajustes`: device install/display/SW status and conditional **Instalar app**. Install event retained until explicit user click, shown after completed demo sitting/session; hidden in standalone mode. No premature push permission request.
- `/debug/viewport`: dev-only lazy implementation. Reports live CSS viewport, visual viewport size/offset, DPR, orientation, breakpoint, browser/standalone, secure context, controller and active/waiting/installing registration state. Manifest request and image decoding verify icon dimensions. Dev deliberately has no new SW registration. Production constant-folds diagnostic module out; direct debug URL shows unavailable page, no diagnostic code.

Future reviewer batch approval, sitting-size preferences and real queue ordering/persistence were not implemented; current fixture has three topic-separated cards. Future exercise types (typed/match/MC/listening), notifications, actual accounts and sync require later milestones. Mock reviewer changes remain local to reviewer component; learner fixture is not mutated by reviewer edits.

## Layout and accessibility intent

- Tailwind-default numeric scale in existing plain CSS: base <640; sm 640; md 768; lg 1024; xl 1280; 2xl 1536 CSS px. Container queries adapt grading/reviewer action rows to actual pane width.
- <768: single learner pane, bottom tabs; reviewer one-column queue and modal bottom sheet. 768–1279: learner session/today panes 60/40; reviewer two columns. ≥1280: session/approved-list panes. ≥1536: reviewer three columns, alternatives right drawer from ≥1280.
- App hard maximum 1600px, session card maximum 42rem/672px at default text size, prose maximum 65ch. Extra width feeds panes and grid, not viewport-scaled fonts.
- `viewport-fit=cover`, independent safe-area insets, 48px minimum buttons/form controls/nav items. Semantic headings, lists, real progress element and skip link.
- Reveal moves focus to answer heading; grading moves focus to next reveal; completion moves to completion heading. Native modal dialog traps focus and supports Escape/cancel; cancel returns focus to opener; resolved queue action focuses queue heading. Edit first field receives focus; cancel returns to Edit button. Status announcements explicitly say example changes are not saved.
- State uses text and ✓/✗/↺/⚑, not color alone. Visible focus styles, 200% root-text check, system-following light/dark palette and toolbar theme media meta, reduced-motion animation removal.

These are design intentions and measured browser behavior, **not a visual approval**. User must confirm composition, contrast, long translated text, phone thumb reach, touch feel and install screenshots on real devices.

## PWA architecture

Manifest: Spanish name/short name, `id/start_url/scope: /`, `lang: es-ES`, standalone, any orientation, warm-paper theme/background, any and maskable 192/512 PNG icons, richer-install narrow/wide screenshots. Deterministic dependency-free PNG generator keeps glyph inside maskable safe zone. Screenshots generated with Chromium from built demo at 390×844 and 2000×1200; no hand-drawn placeholder icons/screenshots. Installation still depends on valid HTTPS and Chrome engagement/eligibility.

Custom Vite build plugin produces `dist/sw.js`; Workbox dependency not necessary for this static/mock-only scope. Final build cache **palabras-shell-ac40295a89928535** derives from SHA-256 of every built file plus worker implementation. 11 files precached, 12 total output files including worker. Public-file changes and worker logic changes bust cache; self is excluded. Hashed JS/CSS, manifest, icons, screenshots, index and real standalone fallback included. Atomic `addAll` avoids activating incomplete shell. Known routes serve same-build index+assets offline; unknown offline routes show `offline.html`. External requests, mutations, `/api/*`, worker and manifest are not runtime cached.

**Chosen update flow: prompt-to-refresh, then explicit `skipWaiting`.** Initial worker activates normally and claims clients. Existing worker keeps coherent cached shell until user sees **Nueva versión disponible** and chooses **Actualizar y reiniciar**, with explicit restart warning. Checks run on app visibility and every visible minute; `updateViaCache: none`. Explicit message activates waiting worker; `controllerchange` reloads confirming tab. Registration failure never prevents app startup. Cleanup deletes only obsolete `palabras-shell-*` caches. Production currently emits one application JS chunk; if later code-splitting into lazy production chunks, retain old deployment assets or coordinate all tabs before cleanup. Never claim real answers survive refresh: demo explicitly does not persist them.

Vite dev/preview set app-side `no-cache` on worker/manifest, verified over HTTP. Static SPA cannot control production ASP.NET headers; required host follow-up above remains blocker.

## Actual verification

Raw complete command output, including initial failed attempts and final reruns: `.opencode/runs/T-20261017-1516/13-ui.log` (workspace root). Final verification starts at log line 230; subsequent rerun covers final test metric-only edit.

- Node v24.21.0, npm 12.2.0.
- `npm ci`: exit 0; audit reports 0 vulnerabilities.
- `npm run type:check`: exit 0.
- `npm run lint`: exit 0.
- `npx tsc -b` (requested `tsc -b` via local pinned compiler): exit 0.
- `npm run build`: exit 0; manifest, all four icons, two screenshots, service worker, real fallback and hashed app assets present.
- `npm run test`: **6/6 pass**. Verifies manifest/PNG/screenshot dimensions, deterministic cache busting, explicit update lifecycle, offline routes/fallback, no private/request interception and built PWA assets/dev-module exclusion.
- `npx playwright install chromium`: installed headless Chromium after initial missing-browser failure.
- `npm run test:browser`: exit 0 against **built production bundle**, plus dev-only debug route. Real Chromium offline navigation, reveal/grade focus, alternatives pick, inline save, 200% text, dark media, no JS page errors, zero backend requests. SW update test replaces served worker with changed cache version; waiting banner preserves current revealed answer until explicit click, then new worker activates and app reloads. Registration failure tested via deliberate register-promise rejection.

### Headless measured geometry (not screenshots judged visually)

- **390×844**: shell 390px, learner/reviewer content column 358px, flashcard 358px, one reviewer column; document scrollWidth 390px at normal and 200% text. Zero tested buttons/nav targets below 44px. Revealed grading stays above bottom navigation and in lower half of viewport. Bottom sheet x0, width390, y173.53, height670.47. Offline approved list and unknown-route fallback both load.
- **2000×1200**: shell1600px; learner panes897.59/598.41px, flashcard672px; reviewer three columns498.66/498.67/498.66px. Drawer x1520,width480,height1200. No horizontal overflow at normal or 200% text; zero tested buttons/nav targets below44px. Same offline/focus/edit checks pass.
- Dev diagnostic resized live390×844→2000×1200, reported2xl and decoded manifest/icons without backend.
- App preview `/sw.js` and `/manifest.webmanifest`: both `Cache-Control: no-cache`.

Initial failures retained in raw log: browser executable absent; implicit label included original struck-through text (fixed accessible name/description); failure-injection changed to explicit register rejection because page network routing does not intercept worker registration; dev harness needed NODE_ENV reset after starting production preview. Final runs clean.

## Files changed — Frontend only

- `Frontend/index.html`, `Frontend/package.json`, `Frontend/package-lock.json`, `Frontend/vite.config.ts`.
- `Frontend/src/App.tsx`, `Frontend/src/style.css`, `Frontend/src/ReviewQueue.tsx`, `Frontend/src/ViewportDebug.tsx`, `Frontend/src/pwa.ts`, `Frontend/src/mock/demo.ts`.
- `Frontend/public/manifest.webmanifest`, `Frontend/public/offline.html`, `Frontend/public/icons/icon-192.png`, `Frontend/public/icons/icon-512.png`, `Frontend/public/icons/maskable-192.png`, `Frontend/public/icons/maskable-512.png`, `Frontend/public/screenshots/narrow.png`, `Frontend/public/screenshots/wide.png`.
- `Frontend/scripts/generate-icons.mjs`, `Frontend/scripts/generate-screenshots.mjs`, `Frontend/scripts/pwa.mjs`, `Frontend/scripts/pwa.d.mts`.
- `Frontend/tests/pwa.test.mjs`, `Frontend/tests/browser.mjs`, `Frontend/HOSTING.md`.

`Frontend/dist` is generated/ignored, not staged. 25 source/public/doc/test paths changed or added. Existing unrelated dirty paths left untouched.

## Next owner actions

1. **implementer/backend owner:** integrate production app-side cache headers and `.webmanifest` MIME type, build/deploy verification. Do not move cache policy to nginx. Follow `Frontend/HOSTING.md`.
2. **User:** open dev `/debug/viewport` on OnePlus 15 and Tab3 in portrait/landscape, return CSS size/DPR/visualViewport measurements. Then HTTPS install, standalone, airplane mode and update prompt checks. Real-device geometry/installability/OxygenOS behavior remain pending. Confirm UI visually; no visual quality claim made.
3. **Orchestrator:** selectively stage only `Frontend/` paths if committing this step; never blanket-add shared dirty tree. Integrate mock boundary with real approved-word/session/reviewer contracts only when backend exists.
