import { createHash } from 'node:crypto'
import { readdir, readFile, writeFile } from 'node:fs/promises'
import { join, relative } from 'node:path'

export async function collectFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true })
  const groups = await Promise.all(entries.map(entry => entry.isDirectory() ? collectFiles(join(directory, entry.name)) : [join(directory, entry.name)]))
  return groups.flat().sort()
}

export function workerSource(version, urls) {
  return `/* Generated from built bytes. Do not edit. */
const CACHE = 'palabras-shell-${version}';
const ASSETS = 'palabras-immutable-assets';
const SHELL = ${JSON.stringify(urls)};
self.addEventListener('install', event => {
  // Complete the entire static cache before switching controllers.
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});
// Every open app tab reloads once on controller replacement (see src/pwa.ts).
self.addEventListener('message', event => {
  if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    // Preserve old hashed chunks for documents already loading during activation.
    // Never preserve old HTML, API responses, or unversioned shell resources.
    const assets = await caches.open(ASSETS);
    for (const key of await caches.keys()) if (key.startsWith('palabras-shell-') && key !== CACHE) {
      const old = await caches.open(key);
      for (const request of await old.keys()) {
        if (/^\\/assets\\/[^/]+-[a-zA-Z0-9_-]+\\.(js|css)$/.test(new URL(request.url).pathname)) {
          const response = await old.match(request);
          if (response?.ok) await assets.put(request, response);
        }
      }
      await caches.delete(key);
    }
    await self.clients.claim();
  })());
});
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || url.pathname === '/api' || url.pathname.startsWith('/api/') || url.pathname === '/sw.js' || url.pathname === '/manifest.webmanifest') return;
  if (event.request.mode === 'navigate') {
    event.respondWith((async () => {
      // The cookie authority wins even if an intermediary has cached HTML.
      // Login remains public; API responses never enter CacheStorage.
      try {
        if (url.pathname !== '/login') {
          const auth = await fetch('/api/auth/me', { credentials: 'include', cache: 'no-store', headers: { Accept: 'application/json' } });
          if (auth.status === 401) return Response.redirect(new URL('/login?next=' + encodeURIComponent(url.pathname + url.search), url.origin), 302);
          if (!auth.ok) return auth;
        }
        return await fetch(event.request, { cache: 'no-store' });
      } catch {
        return (await (await caches.open(CACHE)).match('/offline.htm')) || new Response('Sin conexión. No se puede confirmar la sesión.', { status: 503 });
      }
    })());
  } else if (SHELL.includes(url.pathname) || url.pathname.startsWith('/assets/')) {
    event.respondWith((async () => {
      return (await (await caches.open(CACHE)).match(url.pathname)) || (await (await caches.open(ASSETS)).match(url.pathname)) || fetch(event.request);
    })());
  }
});
`
}

export async function generateWorker(directory) {
  const files = (await collectFiles(directory)).filter(file => !file.endsWith('sw.js'))
  const hash = createHash('sha256')
  // Worker implementation changes must also create a new cache version.
  hash.update(workerSource('VERSION', []))
  for (const file of files) { hash.update(relative(directory, file)); hash.update(await readFile(file)) }
  const version = hash.digest('hex').slice(0, 16)
  // Never precache any app document, even the public login shell.
  // Only neutral offline HTML and public static resources belong in CacheStorage.
  const urls = files.filter(file => !file.endsWith('.html')).map(file => {
    const path = '/' + relative(directory, file).replaceAll('\\', '/')
    return path
  })
  await writeFile(join(directory, 'sw.js'), workerSource(version, urls))
  console.log(`PWA shell ${version}: ${urls.length} precached files; sw.js generated.`)
}
