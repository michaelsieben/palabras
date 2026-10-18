import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getAttemptQueue } from './learn/attemptQueue'
import { LearnSession } from './learn/LearnSession'
import { WordList } from './learn/WordList'
import { StatsPage } from './learn/StatsPage'
import { statsCopy } from './copy/stats'
import { ReviewQueue } from './ReviewQueue'
import { usePwa } from './pwa'
import { useSession } from './api/session'
import { ApiError } from './api/client'
import { Login } from './Login'
import { loginDestination, routeAccess } from './routeAccess'

// Constant-folded out of production bundle, including lazy chunk.
const ViewportDebug = import.meta.env.DEV ? lazy(() => import('./ViewportDebug')) : null

function SessionSkeleton() {
  return <section className="panel auth-skeleton" role="status" aria-label="Comprobando sesión…"><p>Comprobando sesión…</p><div aria-hidden="true"><span /><span /><span /></div></section>
}

export default function App() {
  const [path, setPath] = useState(window.location.pathname)
  const [completed, setCompleted] = useState(false)
  const [logoutError, setLogoutError] = useState('')
  const [loggingOut, setLoggingOut] = useState(false)
  const session = useSession()
  const attemptQueue = useMemo(() => session.user?.role === 'owner' ? getAttemptQueue(session.user.username) : null, [session.user])
  useEffect(() => {
    if (!attemptQueue) return
    attemptQueue.start()
    window.addEventListener('online', attemptQueue.trigger)
    return () => { window.removeEventListener('online', attemptQueue.trigger); attemptQueue.stop() }
  }, [attemptQueue])
  const pwa = usePwa()
  const mainHeading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { const pop = () => setPath(window.location.pathname); window.addEventListener('popstate', pop); return () => window.removeEventListener('popstate', pop) }, [])
  const navigate = useCallback((next: string, replace = false) => { if (replace) history.replaceState(null, '', next); else history.pushState(null, '', next); window.dispatchEvent(new PopStateEvent('popstate')); window.scrollTo(0, 0); if (next.split('?')[0] !== '/login') requestAnimationFrame(() => mainHeading.current?.focus()) }, [])
  const access = routeAccess(path, session.user)
  useEffect(() => {
    if (session.checking || session.error) return
    if (access === 'login') navigate(`/login?next=${encodeURIComponent(path)}`, true)
    else if (path === '/login' && session.user) navigate(loginDestination(window.location.search, session.user), true)
    else if (session.user?.role === 'reviewer' && ['/', '/sesion'].includes(path)) navigate('/revisar', true)
  }, [access, path, session.checking, session.error, session.user, navigate])
  async function signOut() {
    setLoggingOut(true)
    setLogoutError('')
    try { await session.signOut() } catch (error) { if (!(error instanceof ApiError && error.kind === 'unauthorized')) setLogoutError('No se ha podido cerrar la sesión. Inténtalo de nuevo.') } finally { setLoggingOut(false) }
  }
  const authenticated = !session.checking && !session.error && session.user !== null && path !== '/login'
  const reviewing = path === '/revisar' || path === '/aprobadas'
  const canReview = session.user?.role === 'reviewer'
  const reviewerMode = canReview
  const nav = reviewerMode ? [['/revisar', 'Revisar'], ['/aprobadas', 'Aprobadas'], ['/ajustes', 'Ajustes']] : [['/', 'Hoy'], ['/palabras', 'Palabras'], ['/progreso', statsCopy.progress], ['/ajustes', 'Ajustes']]
  return <>
    <a className="skip-link" href="#main">Saltar al contenido</a>
    <div className="app-shell">
      <header className="app-header"><a className="brand" href={canReview ? '/revisar' : '/'} onClick={e => { e.preventDefault(); navigate(canReview ? '/revisar' : '/') }}>p<span>Palabras</span></a>{authenticated && <div className="account-controls" aria-label="Cuenta"><span className="account-name">{session.user?.username}</span><button disabled={loggingOut} onClick={() => void signOut()}>{loggingOut ? 'Cerrando sesión…' : 'Cerrar sesión'}</button></div>}</header>
      {logoutError && authenticated && <p className="auth-error" role="alert">{logoutError}</p>}
      {authenticated && reviewing && <div className="mock-notice"><strong>Demostración revisora</strong> · Datos de ejemplo; cambios de práctica no guardados.</div>}
      <main id="main">
        <div className="page-heading"><div><p className="eyebrow">Español de España · A2</p><h1 tabIndex={-1} ref={mainHeading}>{path === '/debug/viewport' ? 'Viewport · desarrollo' : !authenticated ? 'Palabras, a tu ritmo' : reviewing ? (path === '/aprobadas' ? 'Aprobadas' : 'Un toque de tu español') : path === '/palabras' ? 'Palabras para el día a día' : path === '/progreso' ? statsCopy.progress : path === '/ajustes' ? 'A tu ritmo' : 'Tu repaso de hoy'}</h1></div><span className="chip">España · Pamplona</span></div>
        {path === '/debug/viewport' ? (ViewportDebug ? <Suspense fallback={<p>Cargando diagnóstico…</p>}><ViewportDebug /></Suspense> : <section className="panel"><h2>Página no disponible</h2><p>El diagnóstico solo existe en desarrollo.</p></section>) : session.checking ? <SessionSkeleton /> : session.error ? <section className="panel"><h2>No se ha podido comprobar la sesión</h2><p role="alert">No se puede confirmar si has iniciado sesión. Comprueba la conexión e inténtalo de nuevo.</p><button onClick={() => void session.retry()}>Reintentar</button></section> : path === '/login' ? (!session.user && <Login signIn={session.signIn} />) : access === 'login' ? <SessionSkeleton /> : access === 'forbidden' ? <section className="panel"><h2>Sin permiso</h2><p role="alert">Esta página solo está disponible para la cuenta revisora.</p><button onClick={() => navigate('/')}>Volver a Hoy</button></section> : reviewing ? <ReviewQueue approved={path === '/aprobadas'} onComplete={() => setCompleted(true)} /> : path === '/palabras' ? <WordList /> : path === '/progreso' ? (attemptQueue ? <StatsPage key={session.user?.username} attemptQueue={attemptQueue} /> : <section className="panel"><p role="alert">{statsCopy.forbidden}</p></section>) : path === '/ajustes' ? <section className="panel settings"><h2>Tu app, en este dispositivo</h2><p>{pwa.standalone ? 'Abierta como app instalada' : 'Abierta en el navegador'}</p><p>{pwa.state}</p>{pwa.canInstall && completed ? <button className="primary" onClick={() => void pwa.install()}>Instalar app</button> : !pwa.standalone && <p>Tras terminar una sesión, Chrome ofrecerá la instalación si el sitio cumple los requisitos. También puedes usar el menú de Chrome → Instalar app.</p>}<p>El tema sigue el dispositivo. Sin recordatorios ni ajustes de cuenta todavía.</p>{import.meta.env.DEV && <a href="/debug/viewport" onClick={e => { e.preventDefault(); navigate('/debug/viewport') }}>Diagnóstico de viewport</a>}</section> : ['/', '/sesion'].includes(path) ? <>
          <div className="learner-layout">{attemptQueue && session.user && <LearnSession key={session.user.username} username={session.user.username} attemptQueue={attemptQueue} onComplete={() => setCompleted(true)} />}<aside className="today-pane"><section className="panel today-details"><p className="eyebrow">Un ratito cada día</p><h2>Hoy, paso a paso</h2><p>Repasa primero. Después, descubre palabras nuevas.</p><p>5–15 minutos, sin prisa.</p></section><WordList side /></aside></div>
          {completed && pwa.canInstall && <div className="install-banner"><p>Lleva tus palabras contigo.</p><button className="primary" onClick={() => void pwa.install()}>Instalar app</button></div>}
        </> : <section className="panel"><h2>Página no encontrada</h2><a href="/">Volver a Hoy</a></section>}
      </main>
      {reviewing && <footer className="shell-footer">Revisión de ejemplo · no se sincroniza</footer>}
    </div>
    {authenticated && <nav className="bottom-nav" aria-label={reviewerMode ? 'Navegación revisora' : 'Navegación alumno'}>{nav.map(([url, label]) => <a key={url} href={url} aria-current={path === url || (url === '/' && path === '/sesion') ? 'page' : undefined} onClick={e => { e.preventDefault(); navigate(url) }}>{label}</a>)}</nav>}
  </>
}
