// MOCK ONLY: no API, authentication, FSRS, persistence or sync. Replace this boundary later.
export type AlternativeField = 'gloss' | 'example' | 'variant' | 'lemma'
export type DemoWord = {
  id: string; lemma: string; article: string; gender: string; gloss: string; accepted: string;
  example: string; exampleEn: string; note: string; topic: string; stage: string;
  status: 'approved' | 'pending_review'; agreement: 'alta' | 'media';
  alternatives: { field: AlternativeField; text: string; translation?: string; support: number }[]
}
const makeWord = (id: string, article: string, lemma: string, gloss: string, example: string, exampleEn: string, topic: string, note = ''): DemoWord => ({
  id, article, lemma, gloss, example, exampleEn, topic, note, gender: article === 'la' ? 'f' : 'm',
  accepted: '', stage: 'Reciente', status: 'approved', agreement: 'alta', alternatives: [
    { field: 'gloss', text: `${gloss} (everyday use)`, support: 3 },
    { field: 'gloss', text: `${gloss} (in this context)`, support: 2 },
    { field: 'example', text: `Hablamos de ${article} ${lemma}.`, translation: `We are talking about the ${gloss}.`, support: 2 },
  ],
})
export const approvedWords: DemoWord[] = [
  makeWord('coche', 'el', 'coche', 'car', '¿Vosotros venís en coche a Pamplona?', 'Are you all coming to Pamplona by car?', 'Transporte', 'En España: coche. En LatAm: carro / auto.'),
  makeWord('patata', 'la', 'patata', 'potato', '¿Nos pones unas patatas bravas?', 'Can you bring us some patatas bravas?', 'Comida y bar', 'En España: patata. En LatAm: papa.'),
  makeWord('piso', 'el', 'piso', 'flat', 'Nuestro piso está cerca del centro.', 'Our flat is near the centre.', 'Casa'),
  makeWord('almuerzo', 'el', 'almuerzo', 'mid-morning meal', 'Tomamos el almuerzo después del paseo.', 'We have a mid-morning meal after the walk.', 'Navarra', 'En Navarra, suele ser la comida de media mañana.'),
]
export const reviewWords: DemoWord[] = [
  { ...approvedWords[1], id: 'review-patata', status: 'pending_review', alternatives: [
    { field: 'gloss', text: 'potato', support: 3 }, { field: 'gloss', text: 'potatoes (as a dish)', support: 2 },
    { field: 'example', text: 'Compro patatas en el mercado.', translation: 'I buy potatoes at the market.', support: 3 },
    { field: 'example', text: '¿Queréis una ración de patatas bravas?', translation: 'Would you all like a portion of patatas bravas?', support: 2 },
  ] },
  { ...approvedWords[3], id: 'review-almuerzo', status: 'pending_review', agreement: 'media', alternatives: [
    { field: 'gloss', text: 'mid-morning snack', support: 3 }, { field: 'gloss', text: 'mid-morning meal', support: 2 },
    { field: 'example', text: 'Vamos a almorzar después del encierro.', translation: 'Let’s have a mid-morning meal after the bull run.', support: 3 },
    { field: 'variant', text: 'Uso navarro: comida de media mañana.', support: 2 },
  ] },
  { ...approvedWords[0], id: 'review-coche', status: 'pending_review', alternatives: [
    { field: 'gloss', text: 'car', support: 4 }, { field: 'gloss', text: 'automobile', support: 2 },
    { field: 'example', text: 'Aparcamos el coche cerca de la Plaza del Castillo.', translation: 'We parked the car near the Plaza del Castillo.', support: 3 },
    { field: 'lemma', text: 'automóvil', support: 2 },
  ] },
]
export const demoSummary = { streak: 12, reviews: 18, newWords: 4, minutes: 9 }
