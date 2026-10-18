// O-4, locked 2026-10-18: provisional guesses; revisit after first real sessions.
export const TYPED_SLOW_MS = 12_000
export const RECOGNITION_REVEAL_SLOW_MS = 8_000
export function isSlowAnswer(durationMs: number, mode: 'typed' | 'cloze' | 'recognition'): boolean {
  return durationMs > (mode === 'recognition' ? RECOGNITION_REVEAL_SLOW_MS : TYPED_SLOW_MS)
}

// O-1: Spanish verdict/status chrome; exact-difference explanations stay English.
// One key per §6 row. A future explanation table can replace English at call sites unchanged.
export const englishExplanations = {
  correct: '**{answer}** ({gloss}).',
  'correct-but-slow': '**{answer}**. That took a moment, so it comes back a little sooner.',
  'near-miss-accent': "It's **{answer}**, not *{given}*. Only the accent differs; counted as correct.",
  'accent-required': "It's **{answer}**, not *{given}*. Here the accent changes the word, so it counts.",
  'near-miss-case': "**{answer}**. Only the capital letter differs, and that doesn't count against you.",
  'near-miss-punctuation': '**{answer}**. Only punctuation differs (¿ ¡ and full stops are ignored here).',
  'missing-article': "It's **{answer}**. Add the article: *{article}* tells you it's {genderWord}.",
  'wrong-gender': "It's **{answer}**, not *{given}*. *{headword}* is {genderWord}.",
  wrong: "It's **{answer}** ({gloss}). You wrote *{given}*.",
  skipped: "**{answer}** ({gloss}). It'll come back sooner.",
  'self-override': '**{answer}**. Noted that you overrode the check.',
  'forgot-hint': 'Forgot it? Choose Again.',
  leech: "**{answer}** keeps slipping ({lapses} misses), so it's paused. You can bring it back from Words.",
  'summary-line-1': '{total} cards · {firstTry} right first try · {secondLook} needed a second look{leechSummary}',
  'summary-line-2': 'Tomorrow: ~{tomorrow} due · this week: ~{week} due.',
  'stop-suggestion': 'The last {k} cards were harder going. This is a good place to stop; the rest will keep.',
  'empty-nothing-due': 'Nothing due right now. Next review: {when}.',
  'empty-no-approved-words': "No approved words yet. Words appear here once they're approved.",
  'backlog-warning': '{n} reviews are waiting, so today is reviews only — no new words.',
  'offline-pending': "Offline: {n} answers saved on this phone, sent when you're back online.",
} as const
export type CopyKey = keyof typeof englishExplanations

export const spanishChrome: Record<CopyKey, string> = {
  correct: 'Correcto', 'correct-but-slow': 'Correcto, con esfuerzo',
  'near-miss-accent': 'Casi — cuenta como correcta', 'accent-required': 'Casi — repasa la tilde',
  'near-miss-case': 'Correcto', 'near-miss-punctuation': 'Correcto',
  'missing-article': 'Casi — falta el artículo', 'wrong-gender': 'Casi — revisa el género',
  wrong: 'Esta vez no', skipped: 'Aquí está', 'self-override': 'Cuenta como correcta',
  'forgot-hint': '¿No la recordabas?', leech: 'Palabra en pausa',
  'summary-line-1': 'Resumen de la sesión', 'summary-line-2': 'Próximos repasos',
  'stop-suggestion': 'Puedes parar aquí', 'empty-nothing-due': 'Sin repasos pendientes',
  'empty-no-approved-words': 'Aún no hay palabras aprobadas',
  'backlog-warning': 'Hoy, solo repasos', 'offline-pending': 'Respuestas guardadas sin conexión',
}

export type CopyValues = Readonly<Record<string, string | number | null | undefined>>
// Text nodes, never HTML strings. S9 maps tag to React <strong>/<em> with text children;
// never use dangerouslySetInnerHTML. Parse template BEFORE substitution so user text cannot add markup.
export type CopyNode = { tag: 'text' | 'strong' | 'em'; text: string }
export function renderTemplate(template: string, values: CopyValues = {}): CopyNode[] {
  const substitute = (text: string) => text.replace(/\{(\w+)\}/gu, (placeholder, key: string) =>
    Object.hasOwn(values, key) && values[key] != null ? String(values[key]) : placeholder)
  return template.split(/(\*\*[^*]+\*\*|\*[^*]+\*)/gu).filter(Boolean).map(part => {
    if (part.startsWith('**')) return { tag: 'strong', text: substitute(part.slice(2, -2)) }
    if (part.startsWith('*')) return { tag: 'em', text: substitute(part.slice(1, -1)) }
    return { tag: 'text', text: substitute(part) }
  })
}

export function renderFeedback(key: CopyKey, values: CopyValues = {}, explanations: Readonly<Record<CopyKey, string>> = englishExplanations) {
  const leeches = values.leeches
  const summaryValues = key === 'summary-line-1' ? { ...values,
    leechSummary: typeof leeches === 'number' && leeches > 0 ? ` · ${leeches} new leech` : '' } : values
  return { chrome: spanishChrome[key], chromeLang: 'es' as const,
    explanation: renderTemplate(explanations[key], summaryValues), explanationLang: 'en' as const }
}
