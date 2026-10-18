import { useEffect, useId, useState } from 'react'
import { englishExplanations, spanishChrome } from '../copy/feedback'
import { chooseSpanishVoice } from './voice'

export function SpeakButton({ text, label = 'Escuchar en español de España' }: { text: string; label?: string }) {
  const supported = typeof window !== 'undefined' && 'speechSynthesis' in window
  const noteId = useId()
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([])
  useEffect(() => {
    if (!supported) return
    const synth = window.speechSynthesis
    const update = () => setVoices(synth.getVoices())
    update()
    synth.addEventListener('voiceschanged', update)
    return () => { synth.removeEventListener('voiceschanged', update); synth.cancel() }
  }, [supported])
  const selection = chooseSpanishVoice(voices)
  const noteKey = !selection.voice ? 'audio-no-spanish-voice' : !selection.isPeninsular ? 'audio-spanish-fallback' : null
  if (!supported) return <span className="secondary-text">Audio no disponible en este navegador.</span>
  return <><button type="button" disabled={!selection.voice} onClick={() => {
    // Resolve again inside the gesture: Chrome can populate voices between render and tap.
    const available = window.speechSynthesis.getVoices()
    const current = chooseSpanishVoice(available)
    setVoices(available)
    if (!current.voice) return // Never let a null voice silently select the device default.
    const utterance = new SpeechSynthesisUtterance(text)
    utterance.lang = 'es-ES'
    utterance.voice = current.voice
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(utterance)
  }} aria-label={selection.isPeninsular ? label : spanishChrome[selection.voice ? 'audio-spanish-fallback' : 'audio-no-spanish-voice']}
    aria-describedby={noteKey ? noteId : undefined}>
    {selection.isPeninsular ? 'Escuchar · es-ES' : selection.voice ? spanishChrome['audio-spanish-fallback'] : 'Escuchar'}
  </button>
    {noteKey && <p id={noteId} className="secondary-text" role="status"><span lang="es">{spanishChrome[noteKey]}.</span>{' '}
      <span lang="en">{englishExplanations[noteKey]}</span></p>}
  </>
}
