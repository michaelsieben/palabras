import type { ReviewQueuedRow } from './reviewQueue'
const outcomes = { stale_version: 'La palabra ha cambiado. Actualízala antes de decidir otra vez.', invalid_transition: 'Ya no está en un estado revisable. Actualiza la lista.', invalid: 'No se pudo aplicar esta decisión. Revisa los campos o actualiza la palabra.', applied: 'Decisión confirmada.', duplicate: 'Decisión ya confirmada.' }
export function ReviewRowStatus({ row, retry, refresh, discard, confirmedRefresh = false }: { row: ReviewQueuedRow; retry(): void; refresh(): void; discard(): void; confirmedRefresh?: boolean }) {
  return <div className={`review-row-status ${row.state === 'failed' ? 'review-failure' : ''}`} role="status">
    <p>{row.state === 'pending' ? row.error ? 'Guardada aquí, envío sin confirmar. Se reintentará al conectar.' : 'Decisión guardada en este dispositivo, pendiente de envío.' : row.result ? outcomes[row.result] : 'Envío sin confirmar. La decisión sigue guardada aquí.'}</p>
    {row.state !== 'applied' && <div className="edit-buttons"><button onClick={retry}>Reintentar envío</button><button onClick={refresh}>Actualizar palabra</button>{row.state === 'failed' && <button onClick={discard}>Descartar decisión local</button>}</div>}
    {row.state === 'applied' && confirmedRefresh && <button onClick={refresh}>Actualizar palabra</button>}
  </div>
}
