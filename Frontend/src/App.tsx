import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { approvedWords, demoSummary } from './mock/demo'
import { ReviewQueue } from './ReviewQueue'
import { usePwa } from './pwa'
import { useSession } from './api/session'
import { ApiError } from './api/client'
import { Login } from './Login'
import { loginDestination, routeAccess } from './routeAccess'

// Constant-folded out of production bundle, including lazy chunk.
const ViewportDebug = import.meta.env.DEV ? lazy(() => import('./ViewportDebug')) : null

function Summary() {
  return <section className="summary" aria-label="Resumen de hoy">
    <div><strong>Racha {demoSummary.streak}</strong><span>salto disponible ✓</span></div>
    <div><strong>~{demoSummary.minutes} min</strong><span>{demoSummary.reviews} repasos · {demoSummary.newWords} nuevas</span></div>
    <div className="week" aria-label="Últimos siete días de demostración">{['L', 'M', 'X', 'J', 'V', 'S', 'D'].map((day, i) => <span key={day} className={i < 5 ? 'done' : ''}>{day}<small>{i < 5 ? '✓' : '·'}</small></span>)}</div>
  </section>
}

function WordList({ side = false }: { side?: boolean }) {
  const [search, setSearch] = useState('')
  const [topic, setTopic] = useState('Todas')
  const words = approvedWords.filter(word => `${word.lemma} ${word.gloss}`.toLocaleLowerCase('es').includes(search.toLocaleLowerCase('es')) && (topic === 'Todas' || word.topic === topic))
  return <section className={`panel word-list ${side ? 'side-list' : ''}`} aria-labelledby={side ? 'side-words' : 'words-title'}>
    <p className="eyebrow">Solo palabras aprobadas</p><h2 id={side ? 'side-words' : 'words-title'}>Tus palabras</h2>
    <label>Buscar palabra<input type="search" value={search} onChange={e => setSearch(e.target.value)} placeholder="Español o inglés" /></label>
    <label>Tema<select value={topic} onChange={e => setTopic(e.target.value)}>{['Todas', ...new Set(approvedWords.map(word => word.topic))].map(item => <option key={item}>{item}</option>)}</select></label>
    <ul>{words.map(word => <li key={word.id}><div><strong>{word.article} {word.lemma}</strong><span lang="en">{word.gloss}</span></div><span className="chip">{word.stage}</span></li>)}</ul>
    {!words.length && <p role="status">No hay palabras con este filtro.</p>}
  </section>
}

function Session({ onComplete }: { onComplete(): void }) {
  const [index, setIndex] = useState(0)
  const [revealed, setRevealed] = useState(false)
  const [lastRating, setLastRating] = useState('')
  const revealButton = useRef<HTMLButtonElement>(null)
  const answerHeading = useRef<HTMLHeadingElement>(null)
  const completeHeading = useRef<HTMLHeadingElement>(null)
  const complete = index === approvedWords.length
  const word = approvedWords[index]
  useEffect(() => {
    if (complete) completeHeading.current?.focus()
    else if (revealed) answerHeading.current?.focus()
    else if (index > 0) revealButton.current?.focus()
  }, [revealed, index, complete])
  function grade(rating: string) {
    setLastRating(`Tarjeta ${index + 1}: ${rating}. Valoración de demostración; no guardada.`)
    setRevealed(false)
    setIndex(index + 1)
    if (index + 1 === approvedWords.length) onComplete()
  }
  return <section className="session-pane" aria-label="Sesión de práctica">
    <div className="session-meta"><span>Tarjeta ES → EN</span><span>{Math.min(index + 1, approvedWords.length)} / {approvedWords.length}</span></div>
    <progress max={approvedWords.length} value={index} aria-label="Tarjetas completadas" />
    <div className="flashcard">
      {complete ? <><p className="eyebrow">Sesión de demostración terminada</p><h2 ref={completeHeading} tabIndex={-1}>Bien hecho</h2><p>Un paso más, palabra a palabra.</p><p>La racha y las respuestas no se guardan todavía.</p><button className="primary" onClick={() => { setIndex(0); setRevealed(false); setLastRating('') }}>Otra vuelta</button></> : <>
        <p className="eyebrow">{word.topic} · {word.stage}</p>
        <h2 className="lemma"><span>{word.article}</span> {word.lemma}</h2>
        <p>¿Qué significa en inglés?</p>
        {revealed && <div className="answer"><h3 tabIndex={-1} ref={answerHeading} lang="en">{word.gloss}</h3><p>{word.example}</p><p lang="en" className="secondary-text">{word.exampleEn}</p>{word.note && <p className="note">{word.note}</p>}</div>}
      </>}
    </div>
    <p className="sr-only" role="status">{lastRating}</p>
    {!complete && <div className="session-actions">
      {revealed ? <><p className="grading-hint">¿Cómo te ha ido? Tú eliges.</p><div className="grading-row">
        <button className="again" onClick={() => grade('Again')}><strong>Again</strong><span>No la recordaba ↺</span></button>
        <button onClick={() => grade('Hard')}><strong>Hard</strong><span>Con esfuerzo</span></button>
        <button className="primary" onClick={() => grade('Good')}><strong>Good</strong><span>La sabía ✓</span></button>
        <button onClick={() => grade('Easy')}><strong>Easy</strong><span>Sin dudar</span></button>
      </div></> : <button className="primary reveal" ref={revealButton} onClick={() => setRevealed(true)}>Mostrar respuesta</button>}
    </div>}
  </section>
}

export default function App() {
  const [path, setPath] = useState(window.location.pathname)
  const [completed, setCompleted] = useState(false)
  const [logoutError, setLogoutError] = useState('')
  const [loggingOut, setLoggingOut] = useState(false)
  const session = useSession()
  const pwa = usePwa()
  const mainHeading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { const pop = () => setPath(window.location.pathname); window.addEventListener('popstate', pop); return () => window.removeEventListener('popstate', pop) }, [])
  const navigate = useCallback((next: string, replace = false) => { if (replace) history.replaceState(null, '', next); else history.pushState(null, '', next); window.dispatchEvent(new PopStateEvent('popstate')); window.scrollTo(0, 0); requestAnimationFrame(() => mainHeading.current?.focus()) }, [])
  const access = routeAccess(path, session.user)
  useEffect(() => {
    if (session.checking || session.error) return
    if (access === 'login') navigate(`/login?next=${encodeURIComponent(path)}`, true)
    else if (path === '/login' && session.user) navigate(loginDestination(window.location.search), true)
  }, [access, path, session.checking, session.error, session.user, navigate])
  async function signOut() {
    setLoggingOut(true)
    setLogoutError('')
    try { await session.signOut() } catch (error) { if (!(error instanceof ApiError && error.kind === 'unauthorized')) setLogoutError('No se ha podido cerrar la sesión. Inténtalo de nuevo.') } finally { setLoggingOut(false) }
  }
  const authenticated = !session.checking && !session.error && session.user !== null && path !== '/login'
  const reviewing = path === '/revisar' || path === '/aprobadas'
  const canReview = session.user?.roles.includes('reviewer') === true
  const reviewerMode = reviewing && canReview
  const nav = reviewerMode ? [['/revisar', 'Revisar'], ['/aprobadas', 'Aprobadas'], ['/ajustes', 'Ajustes']] : [['/', 'Hoy'], ['/palabras', 'Palabras'], ['/ajustes', 'Ajustes']]
  return <>
    <a className="skip-link" href="#main">Saltar al contenido</a>
    <div className="app-shell">
      <header className="app-header"><a className="brand" href="/" onClick={e => { e.preventDefault(); navigate('/') }}>p<span>Palabras</span></a>{authenticated && <>{canReview && <a href={reviewerMode ? '/' : '/revisar'} onClick={e => { e.preventDefault(); navigate(reviewerMode ? '/' : '/revisar') }}>{reviewerMode ? 'Vista alumno' : 'Vista revisora'} ↗</a>}<button disabled={loggingOut} onClick={() => void signOut()}>{loggingOut ? 'Cerrando sesión…' : 'Cerrar sesión'}</button></>}</header>
      {logoutError && authenticated && <p className="auth-error" role="alert">{logoutError}</p>}
      <div className="mock-notice"><strong>Demostración</strong> · Datos de ejemplo; cambios de práctica no guardados.</div>
      {pwa.waiting && <aside className="update-banner" role="status"><div><strong>Nueva versión disponible</strong><p>Actualizar reinicia esta demostración. Termina tu tarjeta antes de continuar.</p></div><button className="primary" onClick={pwa.refresh}>Actualizar y reiniciar</button></aside>}
      <main id="main">
        <div className="page-heading"><div><p className="eyebrow">Español de España · A2</p><h1 tabIndex={-1} ref={mainHeading}>{path === '/login' ? 'Iniciar sesión' : reviewing ? (path === '/aprobadas' ? 'Aprobadas' : 'Un toque de tu español') : path === '/palabras' ? 'Palabras para el día a día' : path === '/ajustes' ? 'A tu ritmo' : path === '/debug/viewport' ? 'Viewport · desarrollo' : 'Tu repaso de hoy'}</h1></div><span className="chip">España · Pamplona</span></div>
        {session.checking ? <p role="status">Comprobando sesión…</p> : session.error ? <section className="panel"><h2>No se ha podido comprobar la sesión</h2><p role="alert">Comprueba la conexión e inténtalo de nuevo.</p><button onClick={() => void session.retry()}>Reintentar</button></section> : path === '/login' ? (!session.user && <Login signIn={session.signIn} />) : access === 'login' ? <p role="status">Abriendo inicio de sesión…</p> : access === 'forbidden' ? <section className="panel"><h2>Sin permiso</h2><p role="alert">Esta página solo está disponible para la cuenta revisora.</p><button onClick={() => navigate('/')}>Volver a Hoy</button></section> : path === '/debug/viewport' ? (ViewportDebug ? <Suspense fallback={<p>Cargando diagnóstico…</p>}><ViewportDebug /></Suspense> : <section className="panel"><h2>Página no disponible</h2><p>El diagnóstico solo existe en desarrollo.</p></section>) : reviewing ? <ReviewQueue approved={path === '/aprobadas'} onComplete={() => setCompleted(true)} /> : path === '/palabras' ? <WordList /> : path === '/ajustes' ? <section className="panel settings"><h2>Tu app, en este dispositivo</h2><p>{pwa.standalone ? 'Abierta como app instalada' : 'Abierta en el navegador'}</p><p>{pwa.state}</p>{pwa.canInstall && completed ? <button className="primary" onClick={() => void pwa.install()}>Instalar app</button> : !pwa.standalone && <p>Tras terminar una sesión, Chrome ofrecerá la instalación si el sitio cumple los requisitos. También puedes usar el menú de Chrome → Instalar app.</p>}<p>El tema sigue el dispositivo. Sin recordatorios ni ajustes de cuenta en esta demostración.</p>{import.meta.env.DEV && <a href="/debug/viewport" onClick={e => { e.preventDefault(); navigate('/debug/viewport') }}>Diagnóstico de viewport</a>}</section> : ['/', '/sesion'].includes(path) ? <>
          <Summary /><div className="learner-layout"><Session onComplete={() => setCompleted(true)} /><aside className="today-pane"><section className="panel today-details"><p className="eyebrow">Un ratito cada día</p><h2>Hoy, paso a paso</h2><p>Repasa primero. Después, descubre palabras nuevas.</p><p>5–15 minutos, sin prisa.</p><span className="chip">Racha {demoSummary.streak} · salto disponible ✓</span></section><WordList side /></aside></div>
          {completed && pwa.canInstall && <div className="install-banner"><p>Lleva tus palabras contigo.</p><button className="primary" onClick={() => void pwa.install()}>Instalar app</button></div>}
        </> : <section className="panel"><h2>Página no encontrada</h2><a href="/">Volver a Hoy</a></section>}
      </main>
      <footer className="shell-footer">Práctica local de ejemplo · no se sincroniza</footer>
    </div>
    {authenticated && <nav className="bottom-nav" aria-label={reviewerMode ? 'Navegación revisora' : 'Navegación alumno'}>{nav.map(([url, label]) => <a key={url} href={url} aria-current={path === url || (url === '/' && path === '/sesion') ? 'page' : undefined} onClick={e => { e.preventDefault(); navigate(url) }}>{label}</a>)}</nav>}
  </>
}
