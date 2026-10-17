import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from './api/client'
import type { LoginCredentials } from './api/types'

export function Login({ signIn }: { signIn(credentials: LoginCredentials): Promise<void> }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (loading) return
    setError('')
    setLoading(true)
    try { await signIn({ username, password }) } catch (error) {
      setError(error instanceof ApiError && error.kind === 'unauthorized' ? 'Usuario o contraseña incorrectos.' : error instanceof ApiError && error.kind === 'forbidden' ? 'Sin permiso para iniciar sesión.' : 'No se ha podido iniciar sesión. Comprueba la conexión e inténtalo de nuevo.')
    } finally { setLoading(false) }
  }
  return <section className="panel login-panel" aria-labelledby="login-title">
    <h2 id="login-title">Tu cuenta de Palabras</h2>
    <p>Inicia sesión con tu cuenta existente.</p>
    <form onSubmit={event => void submit(event)} aria-busy={loading}>
      <label>Usuario<input name="username" autoComplete="username" required value={username} disabled={loading} onChange={event => setUsername(event.target.value)} /></label>
      <label>Contraseña<input name="password" type="password" autoComplete="current-password" required value={password} disabled={loading} onChange={event => setPassword(event.target.value)} /></label>
      {error && <p className="auth-error" role="alert">{error}</p>}
      <button className="primary" type="submit" disabled={loading}>{loading ? 'Iniciando sesión…' : 'Iniciar sesión'}</button>
    </form>
  </section>
}
