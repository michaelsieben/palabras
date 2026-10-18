import { ApiError } from '../api/client.ts'
import { getStats, isLocalDate } from '../api/stats.ts'
import type { RetentionSlice } from '../api/stats.ts'
import { statsCopy } from '../copy/stats.ts'

export type StatsRange = 7 | 30 | 90
export function rangeFrom(to: string, days: StatsRange): string {
  if (!isLocalDate(to)) throw new RangeError('Invalid local date')
  return new Date(Date.parse(`${to}T00:00:00Z`) - (days - 1) * 86_400_000).toISOString().slice(0, 10)
}
export function dayLabel(date: string): string {
  if (!isLocalDate(date)) throw new RangeError('Invalid local date')
  return new Intl.DateTimeFormat('es-ES', { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(`${date}T00:00:00Z`))
}
export function percentage(passed: number, total: number): string {
  return total === 0 ? '—' : new Intl.NumberFormat('es-ES', { style: 'percent', maximumFractionDigits: 0 }).format(passed / total)
}
export function retentionLabel(slice: RetentionSlice): string {
  return slice.reviews < 20 ? statsCopy.littleData : percentage(slice.passed, slice.reviews)
}
export function answerTime(ms: number): string {
  const seconds = Math.floor(ms / 1000)
  if (seconds < 60) return `${seconds} s`
  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? `${minutes} min ${seconds % 60} s` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`
}
export function statsError(error: unknown): 'unauthorized' | 'forbidden' | 'error' {
  return error instanceof ApiError && (error.kind === 'unauthorized' || error.kind === 'forbidden') ? error.kind : 'error'
}
// Use the server's local today, never the phone's timezone. No stale/offline cache.
export async function getStatsRange(range: StatsRange, signal?: AbortSignal) {
  const baseline = await getStats({}, signal)
  return range === 30 ? baseline : getStats({ from: rangeFrom(baseline.localDate, range), to: baseline.localDate }, signal)
}
