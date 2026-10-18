import type { Attempt } from '../api/learn'
import { sessionTotals } from './sessionMachine'
import { CopyText } from './CopyText'

export function SessionSummary({ attempts, onAnotherRound }: { attempts: readonly Attempt[]; onAnotherRound(): void }) {
  return <section className="panel session-summary"><CopyText kind="summary-line-1" values={sessionTotals(attempts)} />
    <p>{attempts.length} respuestas en esta sesión, incluidos los repasos repetidos.</p>
    <button className="primary" onClick={onAnotherRound}>Otra vuelta</button>
  </section>
}
