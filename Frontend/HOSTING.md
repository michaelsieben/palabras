# Frontend shell integration

Android Chrome only. Cookie auth uses `/api/auth/login`, `/api/auth/logout`, `/api/auth/me` with `{ username, role: 'owner' | 'reviewer' }` identities, `credentials: include`, `cache: no-store`, and `X-Palabras: 1` on mutations. Passwords exist only in the in-flight form/request and are cleared after submission, never logged or persisted by the app. `/login` has no public registration. Owner opens learner home; reviewer opens `/revisar` automatically. Every API 401 invalidates client identity. No identity/token storage. `src/mock/demo.ts` remains fake learning/review data: no SRS, persisted answers, reminders or real reviewer workflow. Demo changes disappear on reload.

## Required production app headers — host owner follow-up

Vite dev and preview set `Cache-Control: no-cache` on `/sw.js` and `/manifest.webmanifest`. HTTP headers cannot be implemented by a static SPA bundle. The production ASP.NET app must also set these headers while serving `Frontend/dist` as `wwwroot`; nginx must not own this rule. No backend files were changed by this frontend step.

Integrate with the existing static-file middleware (do not add a second middleware blindly):

```csharp
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name is "sw.js" or "manifest.webmanifest" or "index.html")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
```

If the host uses `MapStaticAssets` instead, apply equivalent response headers to those endpoints before response start. Ensure `.webmanifest` is served as `application/manifest+json` and `/sw.js` as JavaScript, never SPA fallback HTML. Routes `/login`, `/`, `/sesion`, `/palabras`, `/revisar`, `/aprobadas`, `/ajustes` need the usual SPA fallback to `index.html`; `/login` must remain public. Do not apply SPA fallback to `/api/*`.

Production acceptance: request `/sw.js` and `/manifest.webmanifest` directly from app and through HTTPS nginx; both must respond with `Cache-Control: no-cache`. This is outstanding until the backend owner integrates and verifies it.

## Offline and updates

Build generates `dist/sw.js` from all output bytes plus worker implementation. Cache name includes SHA-256-derived version. Precache contains **no app HTML or login shell**: only hashed JS/CSS, manifest, four icons, two screenshots and neutral `/offline.htm`. Worker itself is excluded. `.htm` deliberately uses the host's public static-asset path; `.html` pages remain protected. Installation is atomic via `cache.addAll`: a failed response (including 401) rejects installation before activation. Navigations use network `cache: no-store`, never cached shell HTML. Except public `/login`, every navigation first checks authoritative `GET /api/auth/me` with cookies and `cache: no-store`. Authority 401 redirects to network login before requesting any protected document, even when the proxy would hand out stale HTML. Other HTTP auth failures pass through; document HTTP responses including 401/403/500 also pass through. Only rejected network requests show `/offline.htm`, which explicitly cannot confirm a signed-in session. `/api`, `/api/**`, mutations, cross-origin requests, `/sw.js` (including query strings) and `/manifest.webmanifest` bypass fetch interception entirely. Auth responses are never cached. Client auth still checks every app load; no offline authenticated practice. Host must keep `/login`, `/offline.htm`, manifest, icons, screenshots, hashed assets and worker public.

On app load, focus, visible `visibilitychange`, and every visible minute, fetch `/sw.js?check=<fresh UUID>` with `cache: no-store`. SHA-256 of returned worker bytes determines registration `/sw.js?version=<digest>` with explicit root scope and `{ updateViaCache: 'none' }`; also call `registration.update()`. Same bytes keep the same registration URL; changed build/worker bytes produce a fresh proxy cache key. This avoids the observed proxy's stale plain `/sw.js` and ignored no-cache headers. Local tests deliberately keep plain `/sw.js` and each already-seen query cached for 24 hours; replacing actual built worker bytes is picked up without manual cache clearing. A proxy that ignores **all** query strings cannot be defeated by this query-key strategy; production evidence in step 29 shows distinct query responses. Nothing in frontend can update a legacy document that never loads new frontend code.

**Automatic update with atomic install, `skipWaiting`, `clients.claim`, and one reload per controlled app tab** replaces the old prompt. First-ever controller claim does not reload; replacement does, including migration from legacy registration once this client code loads. This prioritizes repairing the auth/navigation boundary without waiting for an insecure old worker to be dismissed. Trade-off: a deployment can reset the current demonstration card. New worker never serves app HTML. Before deleting old shell caches, activation preserves only old hashed JS/CSS in `palabras-immutable-assets` for documents already loading; old HTML/API/unversioned resources are deleted. Both open tabs reload after switching; old chunks remain available so in-flight documents do not lose their matching assets. Preserved assets are browser storage, not account state; they can accumulate across builds and remain subject to browser eviction. Online app remains usable if worker discovery/registration fails.

## Maintainer commands

`npm ci`; `npm run type:check`; `npm run lint`; `npm run build`; `npm run test` (includes built-asset assertions, build first); `npx playwright install chromium`; `npm run test:browser`.

`npm run icons` regenerates deterministic 192/512 PNG any/maskable set. `npm run screenshots` captures install screenshots from existing production build; rebuild afterward to include them in precache. No screenshots are generated by ordinary tests/builds.

## Device acceptance still required

Run dev server with explicit network binding, e.g. `npm run dev -- --host 0.0.0.0`, only on trusted LAN. Open `/debug/viewport` on OnePlus 15 and Tab 3 in both orientations; return CSS dimensions, DPR, orientation and visual viewport numbers. Record those through repo owner. Debug module is compiled out of production; SW disabled in dev. Installation, standalone and offline acceptance require HTTPS production app (or localhost), not unsecured LAN dev server.

Confirm intended phone action reachability, 200% text usability, light/dark contrast, keyboard/focus flow, alternatives sheet vs tablet drawer and install screenshots visually. Headless geometry does not establish visual quality or actual device CSS dimensions.
