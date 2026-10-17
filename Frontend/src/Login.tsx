import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from './api/client'
import type { LoginCredentials } from './api/types'

export function Login({ signIn }: { signIn(credentials: LoginCredentials): Promise<void> }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [rateLimited, setRateLimited] = useState(false)
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (loading) return
    setError('')
    setRateLimited(false)
    setLoading(true)
    try { await signIn({ username, password }) } catch (error) {
      const limited = error instanceof ApiError && error.status === 429
      setRateLimited(limited)
      setError(limited ? 'Espera un momento y vuelve a intentarlo.' : error instanceof ApiError && error.kind === 'unauthorized' ? 'Usuario o contraseña incorrectos.' : 'No se ha podido iniciar sesión. Comprueba la conexión e inténtalo de nuevo.')
    } finally { setPassword(''); setLoading(false) }
  }
  return <section className="panel login-panel" aria-labelledby="login-title">
    <h2 id="login-title">Tu cuenta de Palabras</h2>
    <p>Inicia sesión con tu cuenta existente.</p>
    <form onSubmit={event => void submit(event)} aria-busy={loading}>
      <label>Usuario<input name="username" autoComplete="username" autoFocus autoCapitalize="none" spellCheck={false} required value={username} disabled={loading} onChange={event => setUsername(event.target.value)} /></label>
      <label>Contraseña<input name="password" type="password" autoComplete="current-password" required value={password} disabled={loading} onChange={event => setPassword(event.target.value)} /></label>
      {error && <p className={rateLimited ? 'secondary-text' : 'auth-error'} role={rateLimited ? 'status' : 'alert'}>{error}</p>}
      <button className="primary" type="submit" disabled={loading}>{loading ? 'Iniciando sesión…' : 'Iniciar sesión'}</button>
    </form>
  </section>
}
