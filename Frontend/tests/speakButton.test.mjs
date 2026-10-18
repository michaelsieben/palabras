import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { runInNewContext } from 'node:vm'
import ts from 'typescript'
import * as copy from '../src/copy/feedback.ts'
import * as voice from '../src/learn/voice.ts'

// Exercise component handlers/effect without a DOM or new test dependencies.
function harness(initialVoices = []) {
  let available = initialVoices
  let state = []
  let effect
  let listener
  const spoken = []
  const synth = {
    getVoices: () => available,
    addEventListener: (name, callback) => { assert.equal(name, 'voiceschanged'); listener = callback },
    removeEventListener: (name, callback) => { assert.equal(name, 'voiceschanged'); assert.equal(callback, listener); listener = undefined },
    cancel: () => {},
    speak: utterance => spoken.push(utterance),
  }
  const react = {
    useState: () => [state, value => { state = value }],
    useEffect: callback => { effect = callback },
    useId: () => 'audio-note',
  }
  const jsx = (type, props) => ({ type, props })
  const modules = { react, 'react/jsx-runtime': { jsx, jsxs: jsx, Fragment: 'fragment' },
    '../copy/feedback': copy, './voice': voice }
  const source = ts.transpileModule(readFileSync(new URL('../src/learn/SpeakButton.tsx', import.meta.url), 'utf8'),
    { compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX } }).outputText
  const exports = {}
  runInNewContext(source, { exports, require: name => modules[name], window: { speechSynthesis: synth },
    SpeechSynthesisUtterance: class { constructor(text) { this.text = text } } })
  const render = () => exports.SpeakButton({ text: 'cinco' }).props.children
  render()
  const cleanup = effect()
  return { render, spoken, cleanup, populate: voices => { available = voices },
    voicesChanged: () => listener() }
}

test('empty Chrome list blocks speech until voiceschanged enables a real es_ES voice; listener cleans up', () => {
  const app = harness()
  const [emptyButton] = app.render()
  assert.equal(emptyButton.props.disabled, true)
  emptyButton.props.onClick() // Guard also holds if handler runs despite disabled UI.
  assert.equal(app.spoken.length, 0)
  const spanish = { lang: 'es_ES' }
  app.populate([spanish])
  app.voicesChanged()
  const [button, note] = app.render()
  assert.equal(button.props.disabled, false)
  assert.equal(button.props.children, 'Escuchar · es-ES')
  assert.equal(note, null)
  button.props.onClick()
  assert.equal(app.spoken[0].voice, spanish)
  assert.equal(app.spoken[0].lang, 'es-ES')
  assert.equal(app.spoken[0].text, 'cinco')
  app.cleanup()
})

test('tap re-resolves live voices instead of using stale fallback; disappearing Spanish never speaks default', () => {
  const app = harness([{ lang: 'es-MX' }])
  const [button, note] = app.render()
  assert.equal(button.props['aria-label'], copy.spanishChrome['audio-spanish-fallback'])
  assert.equal(button.props['aria-describedby'], note.props.id)
  assert.equal(note.props.children[2].props.lang, 'en')
  assert.equal(note.props.children[2].props.children, copy.englishExplanations['audio-spanish-fallback'])
  const spanish = { lang: 'es-ES' }
  app.populate([spanish]) // No event yet: first tap must still prefer newly available Spain voice.
  button.props.onClick()
  assert.equal(app.spoken[0].voice, spanish)
  app.populate([{ lang: 'en-US', default: true }])
  app.render()[0].props.onClick()
  assert.equal(app.spoken.length, 1)
  assert.equal(app.render()[0].props.disabled, true)
  app.cleanup()
})
