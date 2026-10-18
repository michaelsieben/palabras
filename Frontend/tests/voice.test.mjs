import test from 'node:test'
import assert from 'node:assert/strict'
import { chooseSpanishVoice } from '../src/learn/voice.ts'

const voice = (lang, flags = {}) => ({ lang, default: false, localService: false, ...flags })

test('hyphens, underscores and mixed case select the same Peninsular locale', () => {
  for (const lang of ['es-ES', 'es_ES', 'ES-es', 'Es_eS']) {
    const target = voice(lang)
    assert.deepEqual(chooseSpanishVoice([voice('es-MX'), target]), { voice: target, isPeninsular: true }, lang)
  }
})

test('exact es-ES outranks es-MX, es-419 and es-US even when they are default/local voices', () => {
  for (const lang of ['es-MX', 'es-419', 'es-US']) {
    const target = voice('es-ES')
    const fallback = voice(lang, { default: true, localService: true })
    assert.deepEqual(chooseSpanishVoice([fallback, target]), { voice: target, isPeninsular: true }, lang)
  }
})

test('exact locale outranks Peninsular extensions; extensions remain Peninsular before any Spanish fallback', () => {
  const extended = voice('ES_es_tradnl')
  const exact = voice('es-ES')
  assert.deepEqual(chooseSpanishVoice([extended, exact]), { voice: exact, isPeninsular: true })
  assert.deepEqual(chooseSpanishVoice([voice('es-MX'), extended]), { voice: extended, isPeninsular: true })
})

test('other Spanish locales are an explicitly non-Peninsular fallback, never device default English', () => {
  for (const lang of ['es-MX', 'es-419', 'es-US', 'ES_ar']) {
    const fallback = voice(lang)
    assert.deepEqual(chooseSpanishVoice([voice('en-US', { default: true }), fallback]),
      { voice: fallback, isPeninsular: false }, lang)
  }
  const lookalike = voice('es-EST')
  assert.deepEqual(chooseSpanishVoice([lookalike]), { voice: lookalike, isPeninsular: false })
})

test('empty or entirely non-Spanish lists produce no voice, not a silent default', () => {
  for (const voices of [[], [voice('en-US', { default: true })], [voice('fr-FR'), voice('de-DE')]]) {
    assert.deepEqual(chooseSpanishVoice(voices), { voice: null, isPeninsular: false })
  }
})

test('non-Spanish tags never match Spanish prefixes, in isolation or ahead of Spanish', () => {
  for (const lang of ['en-ES', 'fr-ES', 'est-EE', 'espanol', 'x-es-ES', 'en-US-es-ES', '']) {
    const other = voice(lang, { default: true, localService: true })
    assert.equal(chooseSpanishVoice([other]).voice, null, lang)
    const spanish = voice('es-US')
    assert.deepEqual(chooseSpanishVoice([other, spanish]), { voice: spanish, isPeninsular: false }, lang)
  }
})

test('selection preserves the actual voice object and does not mutate available voices', () => {
  const target = Object.freeze(voice('es_ES'))
  const voices = Object.freeze([Object.freeze(voice('es-MX')), target])
  assert.equal(chooseSpanishVoice(voices).voice, target)
  assert.equal(target.lang, 'es_ES')
})
