import test from 'node:test'
import assert from 'node:assert/strict'
import * as copy from '../src/copy/feedback.ts'
import { gradeAnswer } from '../src/learn/grade.ts'

const keys = ['correct', 'correct-but-slow', 'near-miss-accent', 'accent-required', 'near-miss-case',
  'near-miss-punctuation', 'missing-article', 'wrong-gender', 'wrong', 'skipped', 'self-override',
  'forgot-hint', 'leech', 'summary-line-1', 'summary-line-2', 'stop-suggestion',
  'empty-nothing-due', 'empty-no-approved-words', 'backlog-warning', 'offline-pending']
const answerKeys = keys.slice(0, 11).concat('leech')
const text = nodes => nodes.map(node => node.text).join('')
const values = { answer: 'la casa', given: 'el casa', gloss: 'house', article: 'la', genderWord: 'feminine',
  headword: 'casa', lapses: 8, n: 4, when: 'tomorrow', total: 10, firstTry: 7, secondLook: 3,
  leeches: 1, tomorrow: 12, week: 30, k: 8 }

test('every §6 row has keyed English explanation and Spanish chrome, rendered with all placeholders', () => {
  assert.deepEqual(Object.keys(copy.englishExplanations), keys)
  assert.deepEqual(Object.keys(copy.spanishChrome), keys)
  for (const key of keys) {
    const rendered = copy.renderFeedback(key, values)
    assert.ok(rendered.chrome.length, key)
    assert.equal(rendered.chromeLang, 'es')
    assert.equal(rendered.explanationLang, 'en')
    assert.ok(rendered.explanation.length, key)
    assert.doesNotMatch(text(rendered.explanation), /\{\w+\}/u, key)
    if (answerKeys.includes(key)) {
      assert.ok(text(rendered.explanation).includes('la casa'), key)
      assert.ok(rendered.explanation.some(node => node.tag === 'strong' && node.text === 'la casa'), key)
    }
  }
  assert.match(text(copy.renderFeedback('missing-article', values).explanation), /Add the article: la tells you it's feminine/u)
  assert.match(text(copy.renderFeedback('wrong-gender', values).explanation), /casa is feminine/u)
})

test('every applicable template retains {answer} when rendering without answer; substitutions remain intact', () => {
  const { answer: omitted, ...rest } = values
  assert.equal(omitted, 'la casa')
  for (const key of answerKeys) {
    assert.ok(copy.englishExplanations[key].includes('{answer}'), key)
    assert.ok(text(copy.renderFeedback(key, rest).explanation).includes('{answer}'), key)
  }
  assert.equal(text(copy.renderTemplate('{answer}: {given}', { answer: '{given}', given: '$&' })), '{given}: $&')
})

test('renderer emits only text-node descriptors, supports strong/em, never trusts user markup or HTML', () => {
  const payload = '<img src=x onerror=alert(1)> **injected** *text*'
  assert.deepEqual(copy.renderTemplate('**{answer}** / *{given}*', { answer: payload, given: payload }), [
    { tag: 'strong', text: payload }, { tag: 'text', text: ' / ' }, { tag: 'em', text: payload },
  ])
  assert.deepEqual(copy.renderTemplate('<script>alert(1)</script>'), [
    { tag: 'text', text: '<script>alert(1)</script>' },
  ])
  assert.deepEqual(copy.renderTemplate(''), [])
})

test('banned phrases absent across every exported string, including nested tables and function source', () => {
  function inspect(value) {
    if (typeof value === 'string' || typeof value === 'function')
      assert.doesNotMatch(String(value), /Wrong!|\bGreat\b|Well done/iu)
    else if (value && typeof value === 'object') Object.values(value).forEach(inspect)
  }
  Object.values(copy).forEach(inspect)
})

test('summary adds leech suffix only above zero and replacement explanation table keeps call shape', () => {
  const render = leeches => text(copy.renderFeedback('summary-line-1', { ...values, leeches }).explanation)
  assert.equal(render(0), '10 cards · 7 right first try · 3 needed a second look')
  assert.equal(render(1), `${render(0)} · 1 new leech`)
  assert.equal(text(copy.renderFeedback('correct', values, { ...copy.englishExplanations, correct: '**{answer}**.' }).explanation), 'la casa.')
})

test('provisional slow thresholds are strict > 12 s typed/cloze and > 8 s recognition reveal', () => {
  assert.equal(copy.TYPED_SLOW_MS, 12_000)
  assert.equal(copy.RECOGNITION_REVEAL_SLOW_MS, 8_000)
  for (const mode of ['typed', 'cloze', 'recognition']) {
    const threshold = mode === 'recognition' ? copy.RECOGNITION_REVEAL_SLOW_MS : copy.TYPED_SLOW_MS
    assert.equal(copy.isSlowAnswer(threshold, mode), false)
    assert.equal(copy.isSlowAnswer(threshold + 1, mode), true)
  }
})

test('production bare/empty noun answers render canonical article and explicit article/gender differences', () => {
  for (const acceptedEs of [[], ['abuela'], ['la abuela']]) {
    const item = { word: { headword: 'abuela', article: 'la', gender: 'feminine' },
      sense: { acceptedEs, gloss: 'grandmother', accentsAreThePoint: false } }
    for (const [given, expectedKind, expectedText] of [
      ['abuela', 'missing-article', "It's la abuela. Add the article: la tells you it's feminine."],
      ['el abuela', 'wrong-gender', "It's la abuela, not el abuela. abuela is feminine."],
      ['la abuela', 'correct', 'la abuela (grandmother).'],
    ]) {
      const result = gradeAnswer(given, item)
      assert.equal(result.kind, expectedKind)
      const rendered = copy.renderFeedback(result.kind, result)
      assert.equal(text(rendered.explanation), expectedText)
      assert.ok(rendered.explanation.some(node => node.tag === 'strong' && node.text === 'la abuela'))
      if (result.answer === given) assert.equal(result.kind, 'correct') // Never false "You wrote" rejection.
    }
  }
})

test('common-gender production noun copy names masculine or feminine rather than leaving placeholder', () => {
  const item = { word: { headword: 'artista', article: 'la', gender: 'common' },
    sense: { acceptedEs: ['artista'], gloss: 'artist', accentsAreThePoint: false } }
  const result = gradeAnswer('artista', item)
  assert.equal(result.kind, 'missing-article')
  assert.equal(text(copy.renderFeedback(result.kind, result).explanation),
    "It's la artista. Add the article: la tells you it's masculine or feminine.")
})
