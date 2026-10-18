import { renderFeedback } from '../copy/feedback'
import type { CopyKey, CopyValues } from '../copy/feedback'

export function CopyText({ kind, values = {} }: { kind: CopyKey; values?: CopyValues }) {
  const copy = renderFeedback(kind, values)
  return <><h3 lang={copy.chromeLang}>{copy.chrome}</h3><p lang={copy.explanationLang}>{copy.explanation.map((node, index) =>
    node.tag === 'strong' ? <strong key={index}>{node.text}</strong> : node.tag === 'em' ? <em key={index}>{node.text}</em> : node.text)}</p></>
}
