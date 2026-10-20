import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import { deviceLabel, deviceStateCopy, retryDeadline, sendTest, windowCopy } from './api'
import { permissionCopy } from './pushClient'
import type { ReminderModel } from './useReminder'

export function ReminderPanel({ model, navigate }: { model: ReminderModel; navigate(path: string): void }) {
  const { reminder, availability, device, outbox } = model
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [failure, setFailure] = useState('')
  const [cooldown, setCooldown] = useState(0)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!cooldown) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [cooldown])
  const waiting = Math.max(0, Math.ceil((cooldown - now) / 1000))
  async function action(work: () => Promise<unknown>, success = 'Cambio guardado en este dispositivo; pendiente de confirmación del servidor.') {
    setBusy(true); setFailure(''); setMessage('')
    try { await work(); setMessage(success); await model.refresh() }
    catch (error) { setFailure(error instanceof Error ? error.message : 'No se pudo guardar. Reintenta antes de cerrar la app.') }
    finally { setBusy(false) }
  }
  async function test() {
    if (!device.endpoint) return
    setBusy(true); setMessage(''); setFailure('')
    try { setMessage(await sendTest(device.endpoint)) }
    catch (error) {
      if (error instanceof ApiError && error.status === 429) { const deadline = retryDeadline(error.retryAfter); setCooldown(deadline); setNow(Date.now()); setFailure('Demasiadas pruebas. Espera antes de volver a enviar.') }
      else if (error instanceof ApiError && error.status === 503) { setFailure('Prueba no disponible: el servicio de recordatorios está desactivado o no puede usar su clave.'); await model.refresh() }
      else setFailure('No se ha podido enviar la prueba. Comprueba la conexión e inténtalo de nuevo.')
    } finally { setBusy(false) }
  }
  return <section className="panel settings reminder-panel" aria-labelledby="reminder-title">
    <h2 id="reminder-title">Recordatorio de repaso</h2>
    <p className="note">Máximo diario: 1 recordatorio por dispositivo. Solo si aún no has completado ni saltado el día. Las pruebas son aparte.</p>
    {model.error && <p role="alert">{model.error}</p>}
    {availability.kind === 'unavailable' ? <p role="status">Recordatorios no disponibles: {availability.reason === 'push_disabled' ? 'el servicio está desactivado.' : 'el servicio no puede usar su clave.'} Puedes guardar tus preferencias, pero no enviar ni probar notificaciones.</p>
      : availability.kind === 'unknown' ? <p role="status">Disponibilidad sin confirmar. No se puede saber cuántos dispositivos pueden recibir recordatorios.</p>
        : reminder && <p>{reminder.activeDevices} {reminder.activeDevices === 1 ? 'dispositivo puede' : 'dispositivos pueden'} recibir recordatorios con la clave actual.{reminder.activeDevices === 0 && device.endpoint ? ' Hay una suscripción local: sincroniza este dispositivo para comprobarla.' : ''}</p>}
    <p>{permissionCopy[device.state]}</p>
    <div className="answer-buttons">
      {availability.kind === 'available' && !['unsupported', 'denied'].includes(device.state) && <button className="primary" disabled={busy} onClick={() => void action(() => model.client!.sync(availability, true))}>{device.state === 'subscribed' ? 'Sincronizar este dispositivo' : 'Activar recordatorio'}</button>}
      <button disabled={busy} onClick={() => void model.refresh()}>Comprobar disponibilidad</button>
    </div>
    {reminder && <form key={`${reminder.enabled}:${reminder.time}`} onSubmit={event => {
      event.preventDefault()
      const values = new FormData(event.currentTarget)
      void action(() => model.queue!.enqueue({ kind: 'reminder', body: { enabled: values.has('enabled'), time: String(values.get('time')) } }))
    }}>
      <label className="check-label"><input name="enabled" type="checkbox" defaultChecked={reminder.enabled} disabled={busy} />Recordatorio activado en tu cuenta</label>
      <label>Hora del recordatorio<input name="time" type="time" min="00:00" max="23:45" step="900" defaultValue={reminder.time} required disabled={busy} /></label>
      <p className="secondary-text">{windowCopy(reminder.time, reminder.windowMin)}</p>
      <p className="secondary-text">Desactivar la cuenta afecta a todos los dispositivos. Silenciar no salta el día. Una notificación ya en camino no se puede retirar.</p>
      <button disabled={busy} type="submit">Guardar preferencias</button>
    </form>}
    {reminder?.mutedUntil && <p className="note">Silenciado hasta el {reminder.mutedUntil}, incluido. Después se reanuda si tu cuenta sigue activada.</p>}
    <div className="answer-buttons"><button onClick={() => navigate('/ajustes/recordatorio')}>Silenciar o reanudar</button><button disabled={busy || waiting > 0 || availability.kind !== 'available' || device.state !== 'subscribed' || outbox.pending > 0 || !device.endpoint} onClick={() => void test()}>{waiting ? `Reintentar prueba en ${waiting} s` : 'Enviar prueba'}</button></div>
    <h3>Dispositivos</h3>
    {model.devicesError && <p role="alert">{model.devicesError}</p>}
    {model.devices ? <>
      <ul className="reminder-devices">{model.devices.items.map(row => <li key={row.id}>
        <strong>{deviceLabel(row, navigator.userAgent)}{row.isCurrent ? ' — Este dispositivo' : ''}</strong>
        <span>{deviceStateCopy(row)}</span>
        {availability.kind === 'available' && <button disabled={busy || outbox.pending > 0 || (row.state === 'rejected' && row.lastStatus === null)} aria-label={`Desactivar dispositivo ${row.id}${row.isCurrent ? ' (este dispositivo)' : ''}`} onClick={() => void action(() => model.revoke(row), 'Baja guardada; pendiente de confirmación del servidor. La lista se actualizará al confirmar.')}>Desactivar</button>}
      </li>)}</ul>
      {!model.devices.items.length && availability.kind === 'available' && <p>No hay suscripciones guardadas en tu cuenta.</p>}
      {model.devices.truncated && <p className="note">Se muestran los primeros 50 dispositivos. Hay más suscripciones guardadas en tu cuenta.</p>}
      <p className="secondary-text">Al volver a activar tras una baja local, aparece una nueva suscripción. La anterior permanece desactivada.</p>
    </> : !model.devicesError && <p role="status">Comprobando los dispositivos guardados…</p>}
    {outbox.storageError ? <p role="alert">No se puede leer el almacenamiento de este dispositivo. No se puede confirmar qué cambios están guardados. Reintenta antes de cerrar la app.</p> : (outbox.pending > 0 || outbox.error) && <p className="note" role="status">{outbox.pending} cambios guardados, pendientes de envío. Se reintentarán al reconectar.<button onClick={model.queue?.trigger}>Reintentar envío</button></p>}
    {outbox.rejected > 0 && <p role="alert">Un cambio no se pudo aplicar ({outbox.reason}). Los dispositivos muestran el último estado confirmado del servidor. Comprueba la conexión y vuelve a intentarlo; para reactivar una suscripción, pulsa Activar o Sincronizar explícitamente.</p>}
    <p role="status">{message}</p>{failure && <p className="auth-error" role="alert">{failure}</p>}
  </section>
}
