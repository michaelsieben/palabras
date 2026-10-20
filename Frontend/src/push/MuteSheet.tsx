import { useState } from 'react'
import type { MuteChoice } from './api'
import type { ReminderModel } from './useReminder'

const choices: [MuteChoice, string][] = [['tomorrow', 'Hasta mañana (mañana incluido)'], ['3d', 'Pausar 3 días'], ['7d', 'Pausar 7 días'], ['disable', 'Desactivar en toda la cuenta'], ['cancel', 'Cancelar silencio y reanudar']]
export function MuteSheet({ model, navigate }: { model: ReminderModel; navigate(path: string): void }) {
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  async function choose(muteChoice: MuteChoice) {
    setBusy(true); setMessage(''); setError('')
    try {
      await model.queue!.enqueue({ kind: 'reminder', body: { muteChoice } })
      setMessage('Elección guardada en este dispositivo. Se aplicará cuando el servidor confirme el envío. Los días se calculan al aplicarla, en la zona horaria de tu cuenta.')
    } catch { setError('No se pudo guardar el cambio. No cierres la app; inténtalo de nuevo.') }
    finally { setBusy(false) }
  }
  return <section className="panel settings mute-sheet" aria-labelledby="mute-title">
    <h2 id="mute-title">Silenciar el recordatorio</h2>
    <p>Silenciar no salta el día. Puedes seguir repasando.</p>
    <p className="secondary-text">El último día indicado queda incluido. Cancelar el silencio no activa una cuenta desactivada. Una notificación ya en camino no se puede retirar.</p>
    {model.reminder?.mutedUntil && <p>Silencio actual: hasta el {model.reminder.mutedUntil}, incluido.</p>}
    <div className="mute-choices">{choices.map(([choice, label]) => <button key={choice} disabled={busy || !model.queue} onClick={() => void choose(choice)}>{label}</button>)}<button onClick={() => navigate('/ajustes')}>Cambiar hora</button><button onClick={() => navigate('/ajustes')}>Volver sin cambios</button></div>
    <p role="status">{message}</p>{error && <p role="alert">{error}</p>}
    {model.outbox.pending > 0 && <p role="status">{model.outbox.pending} cambios pendientes de envío.</p>}
    {model.outbox.rejected > 0 && <p role="alert">No se pudo aplicar el cambio ({model.outbox.reason}). Revisa tu elección.</p>}
    {model.outbox.storageError && <p role="alert">No se puede leer el almacenamiento. No se puede confirmar qué cambios están guardados.</p>}
  </section>
}
