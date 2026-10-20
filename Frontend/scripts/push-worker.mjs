// Serialized into sw.js by pwa.mjs; keep this function self-contained (no module imports).
export function installPushHandlers() {
  const jsonRequest = (method, body) => ({
    method, credentials: 'same-origin', cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', 'X-Palabras': '1' },
    body: JSON.stringify(body),
  })
  const readOptions = { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' } }
  const localDate = value => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value)
    && Number.isFinite(Date.parse(value + 'T12:00:00Z'))
  function targetUrl(value) {
    try {
      if (typeof value !== 'string') throw new Error('missing target')
      const url = new URL(value, self.location.origin)
      if (url.origin === self.location.origin && url.pathname !== '/api' && !url.pathname.startsWith('/api/')) return url.href
    } catch { /* Malformed payloads never navigate outside the app. */ }
    return new URL('/?start=1', self.location.origin).href
  }
  async function openTarget(value) {
    let url = targetUrl(value)
    let result = 'ok'
    try {
      const auth = await fetch('/api/auth/me', readOptions)
      if (auth.status === 401) {
        const target = new URL(url)
        if (target.pathname !== '/login') url = new URL('/login?next=' + encodeURIComponent(target.pathname + target.search), self.location.origin).href
        result = 'unauthenticated'
      } else if (!auth.ok) result = 'error'
    } catch { /* Navigation's existing worker gate supplies the neutral offline page. */ }
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    for (const client of windows) {
      if (new URL(client.url).origin !== self.location.origin) continue
      try {
        await client.focus()
        const navigated = await client.navigate(url)
        if (navigated) return result
      } catch { /* A closing client must not prevent opening another window. */ }
    }
    await self.clients.openWindow(url)
    return result
  }
  async function audit(data, action, result) {
    if (!localDate(data.localDate)) return
    try {
      await fetch('/api/learn/notification-action', jsonRequest('POST', { localDate: data.localDate, action, result }))
    } catch { /* Audit is best effort, never retried. */ }
  }
  self.addEventListener('push', event => {
    event.waitUntil((async () => {
      let payload = {}
      try {
        const parsed = event.data?.json()
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) payload = parsed
      } catch { /* Still display a generic user-visible notification. */ }
      const daily = payload.kind === 'daily'
      const expired = daily && (!Number.isFinite(Date.parse(payload.expiresAt)) || Date.now() >= Date.parse(payload.expiresAt))
      const actions = []
      if (daily && !expired && localDate(payload.localDate)) {
        if (payload.skipAvailable === true) actions.push({ action: 'skip', title: 'Saltar hoy' })
        actions.push({ action: 'mute', title: 'Silenciar' })
      }
      const maxActions = typeof Notification === 'undefined' ? 0 : Notification.maxActions ?? 0
      const next = localDate(payload.skipNextAvailable)
        ? new Intl.DateTimeFormat('es-ES', { weekday: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(payload.skipNextAvailable + 'T12:00:00Z')) : null
      const streak = Number.isInteger(payload.streak) && payload.streak >= 0 ? payload.streak : 0
      const title = expired ? 'Repaso pendiente' : typeof payload.title === 'string' ? payload.title
        : payload.kind === 'test' ? 'Prueba de recordatorio ✓' : 'Tu repaso de hoy'
      const body = expired ? '' : typeof payload.body === 'string' ? payload.body : daily
        ? 'Racha ' + streak + (payload.skipAvailable === true ? ' · salto disponible' : ' · salto usado' + (next ? ' — vuelve el ' + next : '')) : ''
      await self.registration.showNotification(title, {
        body, tag: payload.kind === 'test' ? 'test' : 'daily', renotify: false,
        silent: expired, actions: actions.slice(0, maxActions),
        data: { url: targetUrl(payload.url ?? (payload.kind === 'test' ? '/ajustes' : '/?start=1')), localDate: payload.localDate },
      })
    })().catch(() => { /* Permission revoked or OS display unavailable. */ }))
  })
  self.addEventListener('notificationclick', event => {
    event.notification.close()
    event.waitUntil((async () => {
      const data = event.notification.data ?? {}
      const action = event.action === 'skip' || event.action === 'mute' ? event.action : 'open'
      let result = 'error'
      try {
        if (action === 'skip' && localDate(data.localDate)) {
          const response = await fetch('/api/learn/day/skip', jsonRequest('POST', { localDate: data.localDate, source: 'notification' }))
          let title = 'No se pudo saltar — abre Palabras'
          if (response.ok) { title = 'Saltado ✓ — hasta mañana'; result = 'ok' }
          else if (response.status === 401) {
            result = 'unauthenticated'
            await openTarget('/login?next=/ajustes')
          } else if (response.status === 400) title = 'Ese día ya pasó'
          else if (response.status === 409) {
            const body = await response.json()
            const reason = body.reason ?? body.error
            if (reason === 'skip_unavailable') { title = 'Sin salto disponible — ¿5 min?'; result = reason }
            else if (reason === 'already_done') { title = 'Ya hecho hoy ✓'; result = reason }
          }
          if (response.status !== 401) await self.registration.showNotification(title, {
            tag: 'daily', renotify: false, data: { ...data, url: targetUrl('/?start=1') },
          })
        } else {
          result = await openTarget(action === 'mute' && localDate(data.localDate)
            ? '/ajustes/recordatorio?from=notif&d=' + encodeURIComponent(data.localDate) : data.url)
        }
      } catch { /* Signed-out/offline actions fail once, without unhandled rejections. */ }
      await audit(data, action, result)
    })())
  })
  self.addEventListener('pushsubscriptionchange', event => {
    event.waitUntil((async () => {
      // Only heal an existing subscription, never create initial opt-in from a worker event.
      if (!event.oldSubscription && !event.newSubscription) return
      const response = await fetch('/api/push/config', readOptions)
      if (!response.ok) return // Includes signed out, reviewer, push disabled/unavailable.
      const config = await response.json()
      if (typeof config.publicKey !== 'string' || typeof config.keyId !== 'string') return
      const reminder = await fetch('/api/learn/reminder', readOptions)
      if (!reminder.ok || (await reminder.json()).enabled !== true) return
      const base64 = config.publicKey.replaceAll('-', '+').replaceAll('_', '/')
      const decoded = atob(base64 + '='.repeat((4 - base64.length % 4) % 4))
      const key = Uint8Array.from(decoded, character => character.charCodeAt(0))
      const supplied = event.newSubscription
      const suppliedKey = supplied?.options?.applicationServerKey
      const matchesKey = suppliedKey && new Uint8Array(suppliedKey).length === key.length
        && new Uint8Array(suppliedKey).every((byte, index) => byte === key[index])
      if (supplied && !matchesKey) await supplied.unsubscribe()
      const subscription = supplied && matchesKey ? supplied : await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
      const serialized = subscription.toJSON()
      await fetch('/api/push/subscription', jsonRequest('PUT', {
        endpoint: serialized.endpoint, keys: serialized.keys, keyId: config.keyId,
        replacesEndpoint: event.oldSubscription?.endpoint, reactivate: true,
      }))
    })().catch(() => { /* Chrome may never emit this; authenticated app-open resync heals later. */ }))
  })
}
