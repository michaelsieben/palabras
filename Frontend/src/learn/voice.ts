// Browser voice tags may use underscores instead of BCP-47 hyphens.
export function chooseSpanishVoice<T extends { lang: string }>(voices: readonly T[]): { voice: T | null; isPeninsular: boolean } {
  const normalized = voices.map(voice => ({ voice, lang: voice.lang.replace(/_/gu, '-').toLowerCase() }))
  // Locale correctness outranks device default/localService flags. Keep browser order within each tier.
  const peninsular = normalized.find(({ lang }) => lang === 'es-es')
    ?? normalized.find(({ lang }) => lang.startsWith('es-es-'))
  if (peninsular) return { voice: peninsular.voice, isPeninsular: true }
  const fallback = normalized.find(({ lang }) => lang.startsWith('es-'))
  return { voice: fallback?.voice ?? null, isPeninsular: false }
}
