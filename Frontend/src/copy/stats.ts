// Spanish aggregate/status copy. No grading difference is explained on this screen.
export const statsCopy = {
  progress: 'Progreso', words: 'Palabras', snapshot: 'Estado actual · no depende del periodo',
  inReview: '{n} palabras en repaso', available: 'De {n} palabras aprobadas disponibles.',
  stability: 'La estabilidad es una estimación del planificador, no una prueba de conocimiento.',
  cardPauses: '{leeches} tarjetas con fallos repetidos · en pausa: {manual} manuales y {leech} por fallos repetidos.',
  noWords: 'Aún no hay palabras aprobadas. Aparecerán aquí cuando se aprueben.',
  activity: 'Aciertos / fallos', range: 'Periodo de actividad', rangeDays: '{n} días',
  period: 'Del {from} al {to} · fechas locales de tu cuenta.',
  dayEstimate: 'El reparto por días es aproximado si el reloj del dispositivo estaba desajustado o las respuestas se enviaron tarde sin conexión.',
  hits: 'Aciertos', misses: 'Fallos', formSlips: 'Con fallo de forma', overridden: 'Corregidas por ti',
  answers: 'Respuestas', activeDays: 'Días con respuestas', newWords: 'Palabras nuevas',
  answerRule: 'Cada respuesta cuenta, incluidos los repasos repetidos y la práctica. Un acierto es una respuesta calificada por encima de «Otra vez», incluidas tus correcciones.',
  firstTries: '{first} aciertos a la primera · {second} necesitaron otra mirada.',
  firstScope: 'Se toma la primera respuesta de cada tarjeta por sesión dentro del periodo; si una sesión empieza antes, cuenta su primera respuesta en el periodo. Las respuestas sin sesión cuentan por separado.',
  noAnswers: 'Sin respuestas en este periodo. Envía un repaso para empezar a ver tus aciertos y fallos aquí.',
  start: 'Ir al repaso', retention: 'Retención', retentionEstimate: 'Estimación del recuerdo: proporción de aciertos en tarjetas maduras. Solo repasos aplicados, sin práctica.',
  mature: 'En tarjetas maduras', selfGraded: 'ES → EN · autoevaluada', checked: 'EN → ES · comprobada',
  littleData: 'Pocos datos todavía', sample: '{n} repasos maduros', target: 'Objetivo {n}',
  modes: 'Por ejercicio', modeAccuracy: '{percent} de aciertos · {n} respuestas', noModes: 'Sin ejercicios respondidos en este periodo.',
  hardest: 'Más falladas', noMisses: 'Sin fallos en palabras aprobadas en este periodo.', wordMisses: '{n} fallos',
  answerTime: 'Tiempo respondiendo', estimated: 'Estimación mínima · {time}',
  timeCaveat: 'Solo desde la pregunta hasta mostrar o enviar la respuesta; no incluye leer la explicación ni elegir la calificación. Cada respuesta se limita a 10 minutos.',
  appTime: 'Tiempo total en la app: llega con el registro diario (M7).',
  missingTime: '{n} respuestas sin duración registrada no aportan tiempo a esta estimación.',
  late: '{n} respuestas se enviaron más de 23 horas después. Algunas pueden aparecer en un día distinto al real; es un aviso, no un recuento de fechas erróneas.',
  pending: '{n} respuestas pendientes de envío. Cifras anteriores a su sincronización.',
  offline: 'Progreso disponible al conectar.', loading: 'Cargando progreso…', retry: 'Reintentar',
  error: 'No se ha podido cargar el progreso. Comprueba la conexión e inténtalo de nuevo.',
  unauthorized: 'Inicia sesión para consultar tu progreso.', login: 'Iniciar sesión',
  forbidden: 'El progreso solo está disponible para la cuenta alumna.',
  chartSummary: 'Aciertos y fallos diarios: {hits} aciertos y {misses} fallos. Máximo diario: {max} respuestas. Barras desde cero; los fallos llevan rayas.',
  dailyDetails: 'Consultar cifras de cada día', date: 'Día', max: 'Máximo diario: {n} respuestas',
} as const

export const stageCopy = {
  nuevo: ['Nueva', 'Sin introducir'], aprendiendo: ['Aprendiendo', 'En pasos de aprendizaje'],
  reciente: ['Reciente', 'En repaso'], consolidada: ['Consolidada', 'Estabilidad ≥ 21 días'],
  dominada: ['Dominada', 'Estabilidad ≥ 90 días'],
} as const
export const modeCopy: Readonly<Record<string, string>> = {
  recognition: 'ES → EN · ¿Qué significa en inglés?', typed: 'EN → ES · ¿Cómo se dice en español?', cloze: 'Completa la frase', choice: 'Elegir la respuesta',
}
export function statsText(key: keyof typeof statsCopy, values: Readonly<Record<string, string | number>> = {}) {
  return statsCopy[key].replace(/\{(\w+)\}/gu, (placeholder, name: string) => Object.hasOwn(values, name) ? String(values[name]) : placeholder)
}
