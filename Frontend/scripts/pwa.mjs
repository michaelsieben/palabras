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
const SHELL = ${JSON.stringify(urls)};
const ROUTES = ['/', '/login', '/sesion', '/palabras', '/revisar', '/aprobadas', '/ajustes'];
self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)));
});
// Updates wait until learner explicitly chooses refresh; no silent loss of current card.
self.addEventListener('message', event => {
  if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys()) if (key.startsWith('palabras-shell-') && key !== CACHE) await caches.delete(key);
    await self.clients.claim();
  })());
});
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname === '/sw.js' || url.pathname === '/manifest.webmanifest') return;
  if (event.request.mode === 'navigate') {
    event.respondWith((async () => {
      const cache = await caches.open(CACHE);
      // Keep HTML and hashed chunks from same build, including across a deployment.
      if (ROUTES.includes(url.pathname)) return (await cache.match('/index.html')) || fetch(event.request);
      try { return await fetch(event.request); } catch { return await cache.match('/offline.html'); }
    })());
  } else if (SHELL.includes(url.pathname)) {
    event.respondWith(caches.open(CACHE).then(async cache => (await cache.match(url.pathname)) || fetch(event.request)));
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
  const urls = files.map(file => '/' + relative(directory, file).replaceAll('\\', '/'))
  await writeFile(join(directory, 'sw.js'), workerSource(version, urls))
  console.log(`PWA shell ${version}: ${urls.length} precached files; sw.js generated.`)
}
