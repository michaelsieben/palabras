import { CopyText } from './CopyText'

export function EmptyQueue({ nextDueUtc, retry }: { nextDueUtc?: string; retry(): void }) {
  return <section className="panel"><CopyText kind="empty-nothing-due" values={{ when: nextDueUtc ? new Date(nextDueUtc).toLocaleString('es-ES') : 'not scheduled yet' }} />
    {!nextDueUtc && <p>Las palabras aparecerán cuando estén aprobadas y haya tarjetas disponibles.</p>}
    <button onClick={retry}>Comprobar de nuevo</button>
  </section>
}
