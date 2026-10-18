import test from 'node:test'
import assert from 'node:assert/strict'
import { gradeAnswer, overrideGrade, normalizeAnswer, foldAccents } from '../src/learn/grade.ts'

const card = (answer, overrides = {}) => ({
  word: { headword: answer, article: null, gender: 'not_applicable' },
  sense: { acceptedEs: [answer], gloss: 'coffee', accentsAreThePoint: false },
  ...overrides,
})
const coffee = card('café')
const noun = (article = 'la', gender = 'feminine', headword = 'casa') => card(headword, {
  word: { article, gender, headword },
  sense: { acceptedEs: [`${article} ${headword}`], gloss: 'house', accentsAreThePoint: false },
})

test('every grading outcome carries kind, meaning/form and suggested rating', () => {
  const cases = [
    ['café', coffee, {}, 'correct', true, true, 3],
    ['café', coffee, { isSlow: true }, 'correct-but-slow', true, true, 2],
    ['cafe', coffee, {}, 'near-miss-accent', true, false, 3],
    ['cafe', { ...coffee, sense: { ...coffee.sense, accentsAreThePoint: true } }, {}, 'accent-required', true, false, 1],
    ['CAFÉ', coffee, {}, 'near-miss-case', true, true, 3],
    ['¿café?', coffee, {}, 'near-miss-punctuation', true, true, 3],
    ['casa', noun(), {}, 'missing-article', true, false, 2],
    ['el casa', noun(), {}, 'wrong-gender', true, false, 2],
    ['té', coffee, {}, 'wrong', false, false, 1],
    ['  ', coffee, {}, 'skipped', false, null, 1],
  ]
  for (const [given, item, options, kind, meaning, form, rating] of cases) {
    const actual = gradeAnswer(given, item, options)
    assert.deepEqual([actual.kind, actual.isCorrectMeaning, actual.isCorrectForm, actual.suggestedRating],
      [kind, meaning, form, rating], given)
    assert.equal(actual.given, given)
    assert.equal(actual.gloss, item.sense.gloss)
    assert.equal(actual.answer, item.sense.acceptedEs[0])
  }
  const override = overrideGrade('unexpected alternative', noun())
  assert.deepEqual([override.kind, override.isCorrectMeaning, override.isCorrectForm, override.suggestedRating],
    ['self-override', true, null, 3])
  assert.equal(override.answer, 'la casa')
})

test('NFC/NFD equivalence, es lowercasing, whitespace collapse and exact punctuation set', () => {
  assert.deepEqual(gradeAnswer('cafe\u0301', coffee), { ...gradeAnswer('café', coffee), given: 'cafe\u0301' })
  assert.equal(gradeAnswer('café', card('cafe\u0301')).kind, 'correct')
  assert.equal(normalizeAnswer(` ¿¡.,!?;:"'EL   CAFE\u0301 `), 'el café')
  assert.equal(gradeAnswer('  la\t casa  ', noun()).kind, 'correct')
  assert.equal(gradeAnswer('café-', coffee).kind, 'wrong')
  assert.equal(gradeAnswer('¿CAFÉ?', coffee).kind, 'correct') // Mixed case + punctuation, not either alone.
  assert.equal(gradeAnswer('CAFÉ', coffee, { isSlow: true }).kind, 'near-miss-case')
  assert.equal(gradeAnswer('¿café?', coffee, { isSlow: true }).kind, 'near-miss-punctuation')
})

test('diacritic fold accepts accents and diaeresis but preserves ñ in either normalization', () => {
  assert.equal(foldAccents('áéíóúüñÑ'), 'aeiouuñÑ')
  assert.equal(foldAccents('n\u0303'), 'ñ')
  assert.equal(gradeAnswer('ano', card('año')).kind, 'wrong')
  assert.equal(gradeAnswer('año', card('ano')).kind, 'wrong')
  assert.equal(gradeAnswer('an\u0303o', card('año')).kind, 'correct')
  assert.equal(gradeAnswer('pinguino', card('pingüino')).kind, 'near-miss-accent')
  assert.equal(gradeAnswer('cafè', coffee).kind, 'near-miss-accent')
})

test('accepted alternatives still grade correctly but answer always names canonical full form', () => {
  const item = { ...coffee, sense: { ...coffee.sense, acceptedEs: ['café', 'té'] } }
  assert.equal(gradeAnswer('té', item).kind, 'correct')
  assert.equal(gradeAnswer('té', item).answer, 'café')
  assert.equal(gradeAnswer('te', item).answer, 'café')
  assert.equal(gradeAnswer('casa', { ...noun(), sense: { ...noun().sense, acceptedEs: ['la casa', 'casa'] } }).kind, 'missing-article')
})

test('article checks require same-number swap and exact remaining word; carry gender copy fields', () => {
  for (const [article, other, gender, headword] of [
    ['el', 'la', 'masculine', 'libro'], ['la', 'el', 'feminine', 'casa'],
    ['los', 'las', 'masculine', 'libros'], ['las', 'los', 'feminine', 'casas'],
  ]) {
    const item = noun(article, gender, headword)
    const actual = gradeAnswer(`${other} ${headword}`, item)
    assert.equal(actual.kind, 'wrong-gender')
    assert.equal(actual.article, article)
    assert.equal(actual.genderWord, gender)
    assert.equal(actual.headword, headword)
    assert.equal(gradeAnswer(headword, item).kind, 'missing-article')
  }
  assert.equal(gradeAnswer('los casa', noun()).kind, 'wrong')
  assert.equal(gradeAnswer('el perro', noun()).kind, 'wrong')
  assert.equal(gradeAnswer('el artista', noun('la', 'common', 'artista')).genderWord, 'masculine or feminine')
  assert.equal(gradeAnswer('café', coffee).genderWord, null)
})

test('production bare/empty acceptedEs: omitted article is Hard, not an exact accepted match', () => {
  for (const acceptedEs of [[], ['abuela']]) {
    const item = noun('la', 'feminine', 'abuela')
    item.sense.acceptedEs = acceptedEs
    const actual = gradeAnswer('abuela', item)
    assert.equal(actual.kind, 'missing-article')
    assert.equal(actual.suggestedRating, 2)
    assert.equal(actual.isCorrectMeaning, true)
    assert.equal(actual.isCorrectForm, false)
    assert.equal(actual.answer, 'la abuela')
    assert.equal(actual.article, 'la')
    assert.equal(actual.genderWord, 'feminine')
  }
})

test('production bare/empty acceptedEs: all same-number gender swaps are Hard, cross-number swaps are wrong', () => {
  for (const [article, other, crossNumber, gender, headword] of [
    ['el', 'la', 'las', 'masculine', 'abuelo'], ['la', 'el', 'los', 'feminine', 'abuela'],
    ['los', 'las', 'la', 'masculine', 'abuelos'], ['las', 'los', 'el', 'feminine', 'abuelas'],
  ]) {
    for (const acceptedEs of [[], [headword]]) {
      const item = noun(article, gender, headword)
      item.sense.acceptedEs = acceptedEs
      const actual = gradeAnswer(`${other} ${headword}`, item)
      assert.equal(actual.kind, 'wrong-gender')
      assert.equal(actual.suggestedRating, 2)
      assert.equal(actual.isCorrectMeaning, true)
      assert.equal(actual.isCorrectForm, false)
      assert.equal(actual.answer, `${article} ${headword}`)
      assert.equal(actual.headword, headword)
      assert.equal(actual.genderWord, gender)
      assert.equal(gradeAnswer(`${crossNumber} ${headword}`, item).kind, 'wrong')
    }
  }
})

test('production bare/empty and future article-bearing acceptedEs: right article matches with canonical answer', () => {
  for (const acceptedEs of [[], ['abuela'], ['la abuela'], ['abuela', 'la abuela']]) {
    const item = noun('la', 'feminine', 'abuela')
    item.sense.acceptedEs = acceptedEs
    const actual = gradeAnswer('la abuela', item)
    assert.equal(actual.kind, 'correct')
    assert.equal(actual.suggestedRating, 3)
    assert.equal(actual.isCorrectForm, true)
    assert.equal(actual.answer, 'la abuela')
    assert.equal(gradeAnswer('LA ABUELA', item).kind, 'near-miss-case')
    assert.equal(gradeAnswer('¿la abuela?', item).kind, 'near-miss-punctuation')
    assert.equal(gradeAnswer('abuela', item).kind, 'missing-article')
    assert.equal(gradeAnswer('el abuela', item).kind, 'wrong-gender')
  }
})

test('no-article words accept canonical bare headword with empty or bare acceptedEs; no article fabricated', () => {
  for (const gender of [null, 'not_applicable']) {
    for (const acceptedEs of [[], ['coger']]) {
      const item = card('coger', { word: { headword: 'coger', article: null, gender },
        sense: { acceptedEs, gloss: 'take', accentsAreThePoint: false } })
      const actual = gradeAnswer('coger', item)
      assert.equal(actual.kind, 'correct')
      assert.equal(actual.suggestedRating, 3)
      assert.equal(actual.answer, 'coger')
      assert.equal(actual.article, null)
      assert.equal(actual.genderWord, null)
    }
  }
})

test('canonical full answer survives noun alternatives, accent checks, skip, wrong and override', () => {
  const item = noun('la', 'feminine', 'abuela')
  item.sense.acceptedEs = ['abuela', 'abuelita', 'la abuelita']
  for (const [given, kind] of [['la abuelita', 'correct'], ['abuelita', 'missing-article'],
    ['el abuelita', 'wrong-gender'], ['la abuelíta', 'near-miss-accent'], ['', 'skipped'], ['perro', 'wrong']]) {
    const actual = gradeAnswer(given, item)
    assert.equal(actual.kind, kind)
    assert.equal(actual.answer, 'la abuela')
  }
  assert.equal(overrideGrade('abuelita', item).answer, 'la abuela')
})
