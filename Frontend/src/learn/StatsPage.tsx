import { useEffect, useId, useState, useSyncExternalStore } from 'react'
import type { StatsResponse, RetentionSlice } from '../api/stats'
import { modeCopy, stageCopy, statsCopy, statsText } from '../copy/stats'
import type { getAttemptQueue } from './attemptQueue'
import { stackedBar, stackedColumns } from './svgCharts'
import { answerTime, dayLabel, getStatsRange, percentage, retentionLabel, statsError } from './statsFormat'
import type { StatsRange } from './statsFormat'

const stages = Object.keys(stageCopy) as (keyof typeof stageCopy)[]

function WordStages({ words }: { words: StatsResponse['words'] }) {
  const pattern = useId().replace(/:/gu, '')
  const values = stages.map(stage => words.stages[stage])
  const rects = stackedBar(values, 360, 32)
  const summary = stages.map((stage, index) => `${index + 1}. ${stageCopy[stage][0]}: ${values[index]}`).join('; ')
  return <section className="panel" aria-labelledby="stats-words">
    <p className="eyebrow">{statsCopy.snapshot}</p><h2 id="stats-words">{statsCopy.words}</h2>
    <p className="stats-headline">{statsText('inReview', { n: words.stages.reciente + words.stages.consolidada + words.stages.dominada })}</p>
    <p>{statsText('available', { n: words.approved })}</p>
    {words.approved > 0 ? <>
      <svg className="stats-chart stats-stage-chart" viewBox="0 0 360 32" role="img" aria-label={summary}>
        <defs>{stages.map((stage, index) => <pattern key={stage} id={`${pattern}-${index}`} width={5 + index * 2} height={5 + index * 2} patternUnits="userSpaceOnUse" patternTransform={`rotate(${index * 30})`}><rect width="16" height="16" fill="var(--soft)" /><path d="M0 0V16" stroke="var(--accent)" strokeWidth={index + 1} /></pattern>)}</defs>
        {rects.map(rect => <g key={rect.series}><rect x={rect.x} y={rect.y} width={rect.width} height={rect.height} fill={`url(#${pattern}-${rect.series})`} stroke="var(--surface)" strokeWidth="1" />{rect.width >= 24 && <><rect x={rect.x + rect.width / 2 - 9} y="7" width="18" height="18" rx="4" fill="var(--surface)" /><text x={rect.x + rect.width / 2} y="21" textAnchor="middle" fontSize="12" fill="currentColor">{rect.series + 1}</text></>}</g>)}
      </svg>
      <ol className="stats-stages">{stages.map((stage, index) => <li key={stage}><div><strong>{stageCopy[stage][0]} · {values[index]}</strong><span className="secondary-text">{stageCopy[stage][1]}</span></div></li>)}</ol>
    </> : <p className="note">{statsCopy.noWords}</p>}
    <p className="secondary-text">{statsCopy.stability}</p>
    <p className="secondary-text">{statsText('cardPauses', { leeches: words.leechCards, manual: words.suspendedCards.manual, leech: words.suspendedCards.leech })}</p>
  </section>
}

function DailyAnswers({ data }: { data: StatsResponse }) {
  const pattern = useId().replace(/:/gu, '')
  const rects = stackedColumns(data.days.map(day => [day.hits, day.misses]), 360, 160)
  const maximum = Math.max(0, ...data.days.map(day => day.answers))
  if (!data.totals.answers) return <><p className="note" role="status">{statsCopy.noAnswers}</p><a href="/sesion">{statsCopy.start}</a></>
  return <>
    <p className="secondary-text">{statsText('max', { n: maximum })}</p>
    <svg className="stats-chart" viewBox="0 0 360 162" role="img" aria-label={statsText('chartSummary', { hits: data.totals.hits, misses: data.totals.misses, max: maximum })}>
      <defs><pattern id={pattern} width="6" height="6" patternUnits="userSpaceOnUse"><rect width="6" height="6" fill="var(--warning-bg)" /><path d="M-1 1L1 -1M0 6L6 0M5 7L7 5" stroke="var(--warning)" strokeWidth="2" /></pattern></defs>
      {rects.map(rect => <rect key={`${rect.bucket}-${rect.series}`} x={rect.x} y={rect.y} width={rect.width} height={rect.height} fill={rect.series === 0 ? 'var(--accent)' : `url(#${pattern})`} />)}
      <path d="M0 161H360" stroke="var(--muted)" />
    </svg>
    <div className="stats-axis"><span>0 · {dayLabel(data.from)}</span><span>{dayLabel(data.to)}</span></div>
    <div className="stats-legend"><span><i className="stats-hit" aria-hidden="true" />{statsCopy.hits}</span><span><i className="stats-miss" aria-hidden="true" />{statsCopy.misses}</span></div>
    <details className="stats-details"><summary>{statsCopy.dailyDetails}</summary><table><caption className="sr-only">{statsCopy.activity}</caption><thead><tr><th scope="col">{statsCopy.date}</th><th scope="col">{statsCopy.hits}</th><th scope="col">{statsCopy.misses}</th></tr></thead><tbody>{data.days.map(day => <tr key={day.date}><th scope="row"><time dateTime={day.date}>{dayLabel(day.date)}</time></th><td>{day.hits}</td><td>{day.misses}</td></tr>)}</tbody></table></details>
  </>
}

function RetentionFigure({ label, value }: { label: string; value: RetentionSlice }) {
  return <div className="stats-retention"><h3>{label}</h3><strong>{retentionLabel(value)}</strong><p className="secondary-text">{statsText('sample', { n: value.reviews })}</p></div>
}

export function StatsPage({ attemptQueue }: { attemptQueue: ReturnType<typeof getAttemptQueue> }) {
  const queue = useSyncExternalStore(attemptQueue.subscribe, attemptQueue.getSnapshot)
  const [range, setRange] = useState<StatsRange>(30)
  const [retry, setRetry] = useState(0)
  const [online, setOnline] = useState(navigator.onLine)
  const requestKey = `${range}:${retry}:${online}:${queue.pending}`
  const [result, setResult] = useState<({ requestKey: string } & ({ data: StatsResponse } | { error: ReturnType<typeof statsError> })) | null>(null)
  useEffect(() => {
    const update = () => { setResult(null); setOnline(navigator.onLine) }
    window.addEventListener('online', update); window.addEventListener('offline', update)
    return () => { window.removeEventListener('online', update); window.removeEventListener('offline', update) }
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    if (online) void getStatsRange(range, controller.signal).then(data => {
      if (!controller.signal.aborted) setResult({ requestKey, data })
    }).catch(error => { if (!controller.signal.aborted) setResult({ requestKey, error: statsError(error) }) })
    return () => controller.abort()
  }, [range, online, requestKey])
  // Range/sync changes hide the previous response immediately, without an effect-driven reset.
  const current = result?.requestKey === requestKey ? result : null
  const data = current && 'data' in current ? current.data : null
  const error = current && 'error' in current ? current.error : null
  return <div className="stats-page" lang="es">
    <fieldset className="stats-range"><legend>{statsCopy.range}</legend><div>{([7, 30, 90] as const).map(days => <button key={days} aria-pressed={range === days} onClick={() => { setResult(null); setRange(days) }}>{statsText('rangeDays', { n: days })}</button>)}</div></fieldset>
    {queue.pending > 0 && <p className="note" role="status">{statsText('pending', { n: queue.pending })}</p>}
    {!online ? <section className="panel"><p role="status">{statsCopy.offline}</p></section> : error ? <section className="panel"><p role="alert">{statsCopy[error]}</p>{error === 'unauthorized' ? <a href="/login?next=%2Fprogreso">{statsCopy.login}</a> : error !== 'forbidden' && <button onClick={() => setRetry(value => value + 1)}>{statsCopy.retry}</button>}</section> : !data ? <section className="panel"><p role="status">{statsCopy.loading}</p></section> : <>
      <WordStages words={data.words} />
      <section className="panel" aria-labelledby="stats-activity"><h2 id="stats-activity">{statsCopy.activity}</h2><p className="secondary-text">{statsText('period', { from: dayLabel(data.from), to: dayLabel(data.to) })}</p>
        <dl className="stats-totals">{(['hits', 'misses', 'formSlips', 'overridden', 'answers', 'activeDays', 'newWords'] as const).map(key => <div key={key}><dt>{statsCopy[key]}</dt><dd>{data.totals[key]}</dd></div>)}</dl>
        <DailyAnswers data={data} />
        <p className="secondary-text stats-explanation">{statsCopy.answerRule}</p>
        <p className="secondary-text">{statsCopy.dayEstimate}</p>
        <p>{statsText('firstTries', { first: data.totals.firstTry, second: data.totals.secondLook })}</p><p className="secondary-text">{statsCopy.firstScope}</p>
        {data.quality.lateRows > 0 && <p className="note">{statsText('late', { n: data.quality.lateRows })}</p>}
      </section>
      <section className="panel" aria-labelledby="stats-retention"><h2 id="stats-retention">{statsCopy.retention}</h2><p className="chip">{statsText('target', { n: percentage(data.retention.target, 1) })}</p>
        <RetentionFigure label={statsCopy.mature} value={data.retention.all} /><div className="stats-directions"><RetentionFigure label={statsCopy.selfGraded} value={data.retention.esEn} /><RetentionFigure label={statsCopy.checked} value={data.retention.enEs} /></div><p className="secondary-text">{statsCopy.retentionEstimate}</p>
      </section>
      <section className="panel" aria-labelledby="stats-modes"><h2 id="stats-modes">{statsCopy.modes}</h2><ul className="stats-list">{data.byMode.map(mode => <li key={mode.mode}><strong>{modeCopy[mode.mode] ?? mode.mode}</strong><span>{statsText('modeAccuracy', { percent: percentage(mode.hits, mode.answers), n: mode.answers })}</span></li>)}</ul>{!data.byMode.length && <p>{statsCopy.noModes}</p>}</section>
      <section className="panel" aria-labelledby="stats-hardest"><h2 id="stats-hardest">{statsCopy.hardest}</h2><ol className="stats-list">{data.hardest.map(word => <li key={word.key}><div><a href={`/palabras?q=${encodeURIComponent(word.headword)}`}>{word.article ? `${word.article} ` : ''}{word.headword}</a><span lang="en">{word.gloss}</span></div><span>{statsText('wordMisses', { n: word.misses })}</span></li>)}</ol>{!data.hardest.length && <p>{statsCopy.noMisses}</p>}</section>
      <section className="panel" aria-labelledby="stats-time"><h2 id="stats-time">{statsCopy.answerTime}</h2><p className="stats-headline">{statsText('estimated', { time: answerTime(data.totals.answerMs) })}</p><p className="secondary-text">{statsCopy.timeCaveat}</p>{data.quality.missingDurationRows > 0 && <p className="note">{statsText('missingTime', { n: data.quality.missingDurationRows })}</p>}<p className="secondary-text">{statsCopy.appTime}</p></section>
    </>}
  </div>
}
