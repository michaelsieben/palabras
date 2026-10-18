import { useEffect, useState } from 'react'
import { parseBuildIdentity } from './buildIdentity'
import type { BuildIdentity } from './buildIdentity'

type State = { kind: 'loading' } | { kind: 'ready'; identity: BuildIdentity } | { kind: 'error' }

// /healthz is public and is the only endpoint that names the build actually serving this page, so
// Ajustes reads it directly instead of embedding a second copy of the version in the bundle.
export function BuildInfo() {
  const [state, setState] = useState<State>({ kind: 'loading' })
  useEffect(() => {
    const controller = new AbortController()
    void (async () => {
      try {
        const response = await fetch('/healthz', {
          signal: controller.signal, headers: { Accept: 'application/json' },
          cache: 'no-store', credentials: 'same-origin', redirect: 'error',
        })
        const body: unknown = response.ok ? await response.json() : null
        const identity = parseBuildIdentity(body)
        if (!controller.signal.aborted) setState(identity ? { kind: 'ready', identity } : { kind: 'error' })
      } catch {
        if (!controller.signal.aborted) setState({ kind: 'error' })
      }
    })()
    return () => controller.abort()
  }, [])
  return <section className="build-info" aria-label="Versión instalada">
    <h3>Versión instalada</h3>
    {state.kind === 'ready'
      ? <dl>
        <div><dt>Versión</dt><dd>{state.identity.version}</dd></div>
        <div><dt>Commit</dt><dd><code title={state.identity.commit}>{state.identity.commitShort}</code></dd></div>
        <div><dt>Hito</dt><dd>{state.identity.milestone}</dd></div>
      </dl>
      : <p className="secondary-text">{state.kind === 'loading' ? 'Consultando la versión…' : 'No se ha podido leer la versión del servidor.'}</p>}
  </section>
}
