import { useEffect, useState } from 'react'

export function SpeakButton({ text, label = 'Escuchar en español de España' }: { text: string; label?: string }) {
  const supported = typeof window !== 'undefined' && 'speechSynthesis' in window
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([])
  useEffect(() => {
    if (!supported) return
    const synth = window.speechSynthesis
    const update = () => setVoices(synth.getVoices())
    update()
    synth.addEventListener('voiceschanged', update)
    return () => { synth.removeEventListener('voiceschanged', update); synth.cancel() }
  }, [supported])
  if (!supported) return <span className="secondary-text">Audio no disponible en este navegador.</span>
  return <button type="button" onClick={() => {
    const utterance = new SpeechSynthesisUtterance(text)
    utterance.lang = 'es-ES'
    utterance.voice = voices.find(voice => voice.lang.toLowerCase().startsWith('es-es')) ?? null
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(utterance)
  }} aria-label={label}>Escuchar · es-ES</button>
}
